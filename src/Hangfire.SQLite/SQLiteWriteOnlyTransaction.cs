﻿// This file is part of Hangfire.
// Copyright ?2013-2014 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Annotations;
using Hangfire.SQLite.Entities;
#if NETSTANDARD2_0
using SQLiteConnection = Microsoft.Data.Sqlite.SqliteConnection;
#else
using System.Data.SQLite;
#endif

namespace Hangfire.SQLite
{
    internal class SQLiteWriteOnlyTransaction : JobStorageTransaction
    {
        //private readonly Queue<Action<System.Data.SQLite.SQLiteConnection>> _commandQueue = new Queue<Action<System.Data.SQLite.SQLiteConnection>>();
        private readonly Queue<Action<SQLiteConnection>> _commandQueue = new Queue<Action<SQLiteConnection>>();
        //private readonly SortedSet<string> _lockedResources = new SortedSet<string>();
        private readonly SQLiteStorage _storage;

        public SQLiteWriteOnlyTransaction([NotNull] SQLiteStorage storage)
        {
            if (storage == null) throw new ArgumentNullException("storage");

            _storage = storage;
        }

        public override void Commit()
        {
            _storage.UseTransaction(connection =>
            {
                foreach (var command in _commandQueue)
                {
                    command(connection);
                }
            });
        }

        public override void ExpireJob(string jobId, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
               string.Format(@"update [{0}.Job] set ExpireAt = @expireAt where Id = @id", _storage.GetSchemaName()),
                new { expireAt = DateTime.UtcNow.Add(expireIn), id = jobId }));
        }

        public override void PersistJob(string jobId)
        {
            QueueCommand(x => x.Execute(
                string.Format(@"update [{0}.Job] set ExpireAt = NULL where Id = @id", _storage.GetSchemaName()),
                new { id = jobId }));
        }

        public override void SetJobState(string jobId, IState state)
        {
            string addAndSetStateSql = string.Format(@"
insert into [{0}.State] (JobId, Name, Reason, CreatedAt, Data)
values (@jobId, @name, @reason, @createdAt, @data);
update [{0}.Job] set StateId = last_insert_rowid(), StateName = @name where Id = @id;", _storage.GetSchemaName());

            QueueCommand(x => x.Execute(
                addAndSetStateSql,
                new
                {
                    jobId = jobId,
                    name = state.Name,
                    reason = state.Reason,
                    createdAt = DateTime.UtcNow,
                    data = JobHelper.ToJson(state.SerializeData()),
                    id = jobId
                }));
        }

        public override void AddJobState(string jobId, IState state)
        {
            string addStateSql = string.Format(@"
insert into [{0}.State] (JobId, Name, Reason, CreatedAt, Data)
values (@jobId, @name, @reason, @createdAt, @data)", _storage.GetSchemaName());

            QueueCommand(x => x.Execute(
                addStateSql,
                new
                {
                    jobId = jobId,
                    name = state.Name,
                    reason = state.Reason,
                    createdAt = DateTime.UtcNow,
                    data = JobHelper.ToJson(state.SerializeData())
                }));
        }

        public override void AddToQueue(string queue, string jobId)
        {
            var provider = _storage.QueueProviders.GetProvider(queue);
            var persistentQueue = provider.GetJobQueue();

            QueueCommand(x => persistentQueue.Enqueue(x, queue, jobId));
        }

        public override void IncrementCounter(string key)
        {
            QueueCommand(x => x.Execute(
                string.Format(@"insert into [{0}.Counter] ([Key], [Value]) values (@key, @value)", _storage.GetSchemaName()),
                new { key, value = +1 }));
        }

        public override void IncrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
                string.Format(@"insert into [{0}.Counter] ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)", _storage.GetSchemaName()),
                new { key, value = +1, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public override void DecrementCounter(string key)
        {
            QueueCommand(x => x.Execute(
                string.Format(@"insert into [{0}.Counter] ([Key], [Value]) values (@key, @value)", _storage.GetSchemaName()),
                new { key, value = -1 }));
        }

        public override void DecrementCounter(string key, TimeSpan expireIn)
        {
            QueueCommand(x => x.Execute(
                string.Format(@"insert into [{0}.Counter] ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)", _storage.GetSchemaName()),
                new { key, value = -1, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public override void AddToSet(string key, string value)
        {
            AddToSet(key, value, 0.0);
        }

        public override void AddToSet(string key, string value, double score)
        {
            //            string addSql = string.Format(@"
            //;merge [{0}.Set] as Target
            //using (VALUES (@key, @value, @score)) as Source ([Key], Value, Score)
            //on Target.[Key] = Source.[Key] and Target.Value = Source.Value
            //when matched then update set Score = Source.Score
            //when not matched then insert ([Key], Value, Score) values (Source.[Key], Source.Value, Source.Score);", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(connection =>
            {
                string tableName = string.Format("[{0}.Set]", _storage.GetSchemaName());
                var selectSqlStr = string.Format("select * from {0} where [Key] = @key and Value = @value", tableName);
                var insertSqlStr = string.Format("insert into {0} ([Key], Value, Score) values (@key, @value, @score)", tableName);
                var updateSqlStr = string.Format("update {0} set Score = @score where [Key] = @key and Value = @value ", tableName);

                var fetchedSet = connection.Query<SqlSet>(selectSqlStr,
                    new { key = key, value = value });
                if (!fetchedSet.Any())
                {
                    connection.Execute(insertSqlStr,
                        new { key = key, value, score });
                }
                else
                {
                    connection.Execute(updateSqlStr,
                        new { key = key, value, score });
                }
            });
        }

        public override void RemoveFromSet(string key, string value)
        {
            string query = string.Format(@"delete from [{0}.Set] where [Key] = @key and Value = @value", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(x => x.Execute(
                query,
                new { key, value }));
        }

        public override void InsertToList(string key, string value)
        {
            AcquireListLock();
            QueueCommand(x => x.Execute(
                string.Format(@"insert into [{0}.List] ([Key], Value) values (@key, @value);", _storage.GetSchemaName()),
                new { key, value }));
        }

        public override void RemoveFromList(string key, string value)
        {
            AcquireListLock();
            QueueCommand(x => x.Execute(
                string.Format(@"delete from [{0}.List] where [Key] = @key and Value = @value", _storage.GetSchemaName()),
                new { key, value }));
        }

        public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
        {
            //            string trimSql = string.Format(@"
            //;with cte as (
            //    select row_number() over (order by Id desc) as row_num, [Key] 
            //    from [{0}].List
            //    where [Key] = @key)
            //delete from cte where row_num not between @start and @end", _storage.GetSchemaName());

            string trimSql = string.Format(@"
delete from [{0}.List] where [Key] = @key and Id not in (
  select Id from [{0}.List] where [Key] = @key order by Id desc limit @limit offset @offset)", _storage.GetSchemaName());

            AcquireListLock();
            QueueCommand(x => x.Execute(
                trimSql,
                new { key = key, limit = keepEndingAt - keepStartingFrom + 1, offset = keepStartingFrom }));
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (keyValuePairs == null) throw new ArgumentNullException("keyValuePairs");

            //            string sql = string.Format(@"
            //;merge [{0}.Hash] as Target
            //using (VALUES (@key, @field, @value)) as Source ([Key], Field, Value)
            //on Target.[Key] = Source.[Key] and Target.Field = Source.Field
            //when matched then update set Value = Source.Value
            //when not matched then insert ([Key], Field, Value) values (Source.[Key], Source.Field, Source.Value);", _storage.GetSchemaName());

            AcquireHashLock();
            QueueCommand(connection =>
            {
                string tableName = string.Format("[{0}.Hash]", _storage.GetSchemaName());
                var selectSqlStr = string.Format("select * from {0} where [Key] = @key and Field = @field", tableName);
                var insertSqlStr = string.Format("insert into {0} ([Key], Field, Value) values (@key, @field, @value)", tableName);
                var updateSqlStr = string.Format("update {0} set Value = @value where [Key] = @key and Field = @field ", tableName);
                foreach (var keyValuePair in keyValuePairs)
                {
                    var fetchedHash = connection.Query<SqlHash>(selectSqlStr,
                        new { key = key, field = keyValuePair.Key });
                    if (!fetchedHash.Any())
                    {
                        connection.Execute(insertSqlStr,
                            new { key = key, field = keyValuePair.Key, value = keyValuePair.Value });
                    }
                    else
                    {
                        connection.Execute(updateSqlStr,
                            new { key = key, field = keyValuePair.Key, value = keyValuePair.Value });
                    }
                }
            });
        }

        public override void RemoveHash(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"delete from [{0}.Hash] where [Key] = @key", _storage.GetSchemaName());

            AcquireHashLock();
            QueueCommand(x => x.Execute(query, new { key }));
        }

        public override void AddRangeToSet(string key, IList<string> items)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (items == null) throw new ArgumentNullException("items");

            string query = string.Format(@"
insert into [{0}.Set] ([Key], Value, Score)
values (@key, @value, 0.0)", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(x => x.Execute(query, items.Select(value => new { key = key, value = value }).ToList()));
        }

        public override void RemoveSet(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"delete from [{0}.Set] where [Key] = @key", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(x => x.Execute(query, new { key = key }));
        }

        public override void ExpireHash(string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.Hash] set ExpireAt = @expireAt where [Key] = @key", _storage.GetSchemaName());

            AcquireHashLock();
            QueueCommand(x => x.Execute(query, new { key = key, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public override void ExpireSet(string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.Set] set ExpireAt = @expireAt where [Key] = @key", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(x => x.Execute(query, new { key = key, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public override void ExpireList(string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.List] set ExpireAt = @expireAt where [Key] = @key", _storage.GetSchemaName());

            AcquireListLock();
            QueueCommand(x => x.Execute(query, new { key = key, expireAt = DateTime.UtcNow.Add(expireIn) }));
        }

        public override void PersistHash(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.Hash] set ExpireAt = null where [Key] = @key", _storage.GetSchemaName());

            AcquireHashLock();
            QueueCommand(x => x.Execute(query, new { key = key }));
        }

        public override void PersistSet(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.Set] set ExpireAt = null where [Key] = @key", _storage.GetSchemaName());

            AcquireSetLock();
            QueueCommand(x => x.Execute(query, new { key = key }));
        }

        public override void PersistList(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            string query = string.Format(@"
update [{0}.List] set ExpireAt = null where [Key] = @key", _storage.GetSchemaName());

            AcquireListLock();
            QueueCommand(x => x.Execute(query, new { key = key }));
        }

        internal void QueueCommand(Action<IDbConnection> action)
        {
            _commandQueue.Enqueue(action);
        }

        private void AcquireListLock()
        {
            AcquireLock(String.Format("Hangfire:List:Lock"));
        }

        private void AcquireSetLock()
        {
            AcquireLock(String.Format("Hangfire:Set:Lock"));
        }

        private void AcquireHashLock()
        {
            AcquireLock(String.Format("Hangfire:Hash:Lock"));
        }

        private void AcquireLock(string resource)
        {

        }
    }
}