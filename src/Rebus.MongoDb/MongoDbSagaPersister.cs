﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Timers;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using MongoDB.Bson;
using System.Linq;
using Rebus.Logging;

namespace Rebus.MongoDb
{
    /// <summary>
    /// MongoDB implementation of Rebus' <see cref="IStoreSagaData"/>. Please note that MongoDB does
    /// not support two-phase commit, which instead gets simulated by enlisting in the ambient transaction
    /// when one is present, delaying the Save/Delete operation until the commit phase.
    /// </summary>
    public class MongoDbSagaPersister : IStoreSagaData
    {
        const string IdElementName = "_id";

        static readonly SagaDataElementNameConvention ElementNameConventions;
        static readonly string RevisionMemberName;
        static ILog log;

        static MongoDbSagaPersister()
        {
            RebusLoggerFactory.Changed += f => log = f.GetCurrentClassLogger();

            // try to use our own naming convention
            var namingConvention = new SagaDataElementNameConvention();
            RevisionMemberName = namingConvention.RevisionMemberName;
            ElementNameConventions = namingConvention;
            var conventionProfile = new ConventionProfile().SetElementNameConvention(ElementNameConventions);
            BsonClassMap.RegisterConventions(conventionProfile, t => typeof(ISagaData).IsAssignableFrom(t));
        }

        readonly Dictionary<Type, string> collectionNames = new Dictionary<Type, string>();
        readonly MongoDatabase database;
        readonly Timer indexRecreationTimer = new Timer();

        /// <summary>
        /// We keep track whether the index has been declared recently in order to minimize the risk that someone
        /// accidentally removes the unique constraint behind our back
        /// </summary>
        volatile bool indexEnsuredRecently;
        readonly object indexEnsuredRecentlyLock = new object();

        bool allowAutomaticSagaCollectionNames;

        /// <summary>
        /// Constructs the persister which will connect to the Mongo database pointed to by the connection string.
        /// This also means that the connection string must include the database name.
        /// </summary>
        public MongoDbSagaPersister(string connectionString)
        {
            log.Info("Connecting to Mongo");
            database = MongoHelper.GetDatabase(connectionString);

            // flick the bool once in a while
            indexRecreationTimer.Elapsed += delegate { indexEnsuredRecently = false; };
            indexRecreationTimer.Interval = TimeSpan.FromMinutes(1).Milliseconds;
            indexRecreationTimer.Start();
        }

        /// <summary>
        /// Tells the persister that it's ok that it comes up with collection names for saga data by itself. This
        /// lowers the friction, but since the saga data type name is used to come up with a collection name, it
        /// would cause weird behaviour if you renamed a saga data class.
        /// </summary>
        public MongoDbSagaPersister AllowAutomaticSagaCollectionNames()
        {
            log.Info("Saga persister will figure out Mongo collection names by convention");
            allowAutomaticSagaCollectionNames = true;
            return this;
        }

        /// <summary>
        /// Tells the persister to store saga data of the specified type in the collection with the given name
        /// </summary>
        public MongoDbSagaPersister SetCollectionName<TSagaData>(string collectionName) where TSagaData : ISagaData
        {
            var sagaDataType = typeof (TSagaData);

            if (collectionNames.ContainsKey(sagaDataType))
            {
                var errorMessage =
                    string.Format("Attempted to set the collection name of saga data of type {0} to {1}, but it was" +
                                  " already set to {2}! It is not permitted to set the collection name twice, because" +
                                  " this is most likely be an indication that some initialization part of your code is" +
                                  " running twice, which could have unintended consequences",
                                  sagaDataType, collectionName, collectionNames[sagaDataType]);
                
                throw new InvalidOperationException(errorMessage);
            }

            log.Info("Saga data of type {0} will be stored in collection named {1}", sagaDataType, collectionName);

            collectionNames.Add(sagaDataType, collectionName);
            
            return this;
        }

        public void Insert(ISagaData sagaData, string[] sagaDataPropertyPathsToIndex)
        {
            var collection = database.GetCollection(GetCollectionName(sagaData.GetType()));

            EnsureIndexHasBeenCreated(sagaDataPropertyPathsToIndex, collection);

            sagaData.Revision++;
            try
            {
                var writeConcernResult = collection.Insert(sagaData, WriteConcern.Acknowledged);

                EnsureResultIsGood(writeConcernResult,
                       "insert saga data of type {0} with _id {1} and _rev {2}", 0,
                       sagaData.GetType(),
                       sagaData.Id,
                       sagaData.Revision);
            }
            catch (MongoSafeModeException ex)
            {
                // in case of race conditions, we get a duplicate key error because the upsert
                // cannot proceed to insert a document with the same _id as an existing document
                // ... therefore, we map the MongoSafeModeException to our own OptimisticLockingException
                throw new OptimisticLockingException(sagaData, ex);
            }
        }

        public void Update(ISagaData sagaData, string[] sagaDataPropertyPathsToIndex)
        {
            var collection = database.GetCollection(GetCollectionName(sagaData.GetType()));

            EnsureIndexHasBeenCreated(sagaDataPropertyPathsToIndex, collection);

            var revisionElementName = GetRevisionElementName(sagaData);

            var criteria = Query.And(Query.EQ(IdElementName, sagaData.Id),
                                     Query.EQ(revisionElementName, sagaData.Revision));

            sagaData.Revision++;

            var update = MongoDB.Driver.Builders.Update.Replace(sagaData);
            try
            {
                var safeModeResult = collection.Update(criteria, update, WriteConcern.Acknowledged);

                EnsureResultIsGood(safeModeResult,
                       "update saga data of type {0} with _id {1} and _rev {2}", 1,
                       sagaData.GetType(),
                       sagaData.Id,
                       sagaData.Revision);
            }
            catch (MongoSafeModeException ex)
            {
                // in case of race conditions, we get a duplicate key error because the upsert
                // cannot proceed to insert a document with the same _id as an existing document
                // ... therefore, we map the MongoSafeModeException to our own OptimisticLockingException
                throw new OptimisticLockingException(sagaData, ex);
            }
        }

        public void Delete(ISagaData sagaData)
        {
            var collection = database.GetCollection(GetCollectionName(sagaData.GetType()));

            var revisionElementName = GetRevisionElementName(sagaData);

            var query = Query.And(Query.EQ(IdElementName, sagaData.Id),
                                  Query.EQ(revisionElementName, sagaData.Revision));

            try
            {
                var safeModeResult = collection.Remove(query, WriteConcern.Acknowledged);

                EnsureResultIsGood(safeModeResult,
                                   "delete saga data of type {0} with _id {1} and _rev {2}", 1,
                                   sagaData.GetType(),
                                   sagaData.Id,
                                   sagaData.Revision);
            }
            catch (MongoSafeModeException ex)
            {
                // in case of race conditions, we get a duplicate key error because the upsert
                // cannot proceed to insert a document with the same _id as an existing document
                // ... therefore, we map the MongoSafeModeException to our own OptimisticLockingException
                throw new OptimisticLockingException(sagaData, ex);
            }
        }

        public T Find<T>(string sagaDataPropertyPath, object fieldFromMessage) where T : class, ISagaData
        {
            var collection = database.GetCollection(typeof(T), GetCollectionName(typeof(T)));

            if (sagaDataPropertyPath == "Id")
                return collection.FindOneByIdAs<T>(BsonValue.Create(fieldFromMessage));

            var query = Query.EQ(MapSagaDataPropertyPath(sagaDataPropertyPath, typeof(T)), BsonValue.Create(fieldFromMessage));
            return collection.FindOneAs<T>(query);
        }

        string GetCollectionName(Type sagaDataType)
        {
            if (collectionNames.ContainsKey(sagaDataType))
            {
                return collectionNames[sagaDataType];
            }

            if (allowAutomaticSagaCollectionNames)
            {
                return GenerateAutoSagaCollectionName(sagaDataType);
            }

            throw new InvalidOperationException(
                string.Format(
                    @"This MongoDB saga persister doesn't know where to store sagas of type {0}.

You must specify a collection for each saga type on the persister, e.g. like so:

    new MongoDbSagaPersister(ConnectionString)
        .SetCollectionName<MySagaData>(""my_sagas"")
        .SetCollectionName<MyOtherSagaData>(""my_other_sagas"");

if you create the persister manually, or like this if you're using the fluent configuration API:

    Configure.With(adapter)
        (...)
        .Sagas(s => s.StoreInMongoDb(ConnectionString)
                        .SetCollectionName<SomeKindOfSaga>(""some_kind_of_saga"")
                        .SetCollectionName<AnotherKindOfSaga>(""another_kind_of_saga""))
        (...)

Alternatively, if you're more into the ""convention over configuration"" thing, trade a little bit of control for reduced friction, and let the persister come up with a name by itself:

    Configure.With(adapter)
        (...)
        .Sagas(s => s.StoreInMongoDb(ConnectionString)
                        .AllowAutomaticSagaCollectionNames())
        (...)

which will make the persister use the type of the saga to come up with collection names automatically - for sagas of the type {0}, the collection would be named '{1}'.
",
                    sagaDataType, GenerateAutoSagaCollectionName(sagaDataType)));
        }

        static string GenerateAutoSagaCollectionName(Type sagaDataType)
        {
            return string.Format("sagas_{0}", sagaDataType.Name);
        }

        void EnsureIndexHasBeenCreated(IEnumerable<string> sagaDataPropertyPathsToIndex, MongoCollection<BsonDocument> collection)
        {
            if (!indexEnsuredRecently)
            {
                lock (indexEnsuredRecentlyLock)
                {
                    if (!indexEnsuredRecently)
                    {
                        log.Info("Re-declaring indexes with unique constraints for the following paths: {0}", string.Join(", ", sagaDataPropertyPathsToIndex));

                        collection.ResetIndexCache();

                        //                        var indexes = collection.GetIndexes();

                        foreach (var propertyToIndex in sagaDataPropertyPathsToIndex.Except(new[] { "Id" }))
                        {
                            var indexDefinition = IndexKeys.Ascending(propertyToIndex);

                            //if (IndexAlreadyExists(indexes, propertyToIndex))
                            //{
                            //    AssertIndexIsCorrect(indexes, propertyToIndex);
                            //}

                            collection.EnsureIndex(indexDefinition,
                                                   IndexOptions.SetBackground(false).SetUnique(true));
                        }

                        //collection.ReIndex();

                        indexEnsuredRecently = true;
                    }
                }
            }
        }

        void AssertIndexIsCorrect(GetIndexesResult indexes, string propertyToIndex)
        {
            var indexInfo = indexes.Single(i => IsIndexForProperty(propertyToIndex, i));

            if (!indexInfo.IsUnique)
            {
                throw new InvalidOperationException(string.Format("The index for {0} already existed, but it wasn't enforcing a UNIQUE constraint.", propertyToIndex));
            }

            if (indexInfo.IsBackground)
            {
                throw new InvalidOperationException(string.Format("The index for {0} aready exists, but it wasn't SYNCHRONOUS."));
            }
        }

        /// <summary>
        /// Asks the BSON serializer what is the Mongo element name for the revision
        /// property of saga data of the given type
        /// </summary>
        static string GetRevisionElementName(ISagaData sagaData)
        {
            var revisionElementName = "_rev";

            var classmap = BsonClassMap.LookupClassMap(sagaData.GetType());
            var revision = classmap.GetMemberMap(RevisionMemberName);
            if (revision != null)
            {
                revisionElementName = revision.ElementName;
            }

            return revisionElementName;
        }

        bool IndexAlreadyExists(IEnumerable<IndexInfo> indexes, string propertyToIndex)
        {
            return indexes
                .Any(indexInfo => IsIndexForProperty(propertyToIndex, indexInfo));
        }

        static bool IsIndexForProperty(string propertyToIndex, IndexInfo indexInfo)
        {
            var indexKeys = indexInfo.Key.ToDictionary();

            return indexKeys.Count == 1 && indexKeys.ContainsKey(propertyToIndex);
        }

        string MapSagaDataPropertyPath(string sagaDataPropertyPath, Type sagaDataType)
        {
            var propertyInfo = sagaDataType.GetProperty(sagaDataPropertyPath, BindingFlags.Public | BindingFlags.Instance);

            if (propertyInfo == null)
                return sagaDataPropertyPath;

            return ElementNameConventions.GetElementName(propertyInfo);
        }

        void EnsureResultIsGood(WriteConcernResult writeConcernResult, string message, int expectedNumberOfAffectedDocuments, params object[] objs)
        {
            if (!writeConcernResult.Ok)
            {
                var exceptionMessage = string.Format("Tried to {0}, but apparently the operation didn't succeed.",
                                                     string.Format(message, objs));

                throw new MongoSafeModeException(exceptionMessage, writeConcernResult);
            }

            if (writeConcernResult.DocumentsAffected != expectedNumberOfAffectedDocuments)
            {
                var exceptionMessage = string.Format("Tried to {0}, but documents affected != {1}.",
                                                     string.Format(message, objs),
                                                     expectedNumberOfAffectedDocuments);

                throw new MongoSafeModeException(exceptionMessage, writeConcernResult);
            }
        }
    }
}
