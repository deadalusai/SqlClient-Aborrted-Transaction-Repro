using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace ZombieTester
{
    class Program
    {
        static void Main(string[] args)
        {
            IDbConnection OpenDbConnection()
            {
                #if NETCOREAPP3_1
                var connection = Microsoft.Data.SqlClient.SqlClientFactory.Instance.CreateConnection();
                #else
                var connection = System.Data.SqlClient.SqlClientFactory.Instance.CreateConnection();
                #endif
                connection.ConnectionString = "Application Name=ZombieTester;Server=.;Database=Zombie;Trusted_Connection=True";
                connection.Open();
                return connection;
            }

            while (true)
            {
                Console.WriteLine("Press any key to run experiment...");
                Console.ReadKey(true);

                var maxTestDuration = TimeSpan.FromMinutes(1);
                var timer = System.Diagnostics.Stopwatch.StartNew();

                while (timer.Elapsed < maxTestDuration)
                {
                    using (var conn = OpenDbConnection())
                    {
                        System.Console.WriteLine($"Using {conn.GetType().Namespace}");
                        CleanZombieRecords(conn);
                    }

                    var tasks = Enumerable.Range(0, 2)
                        .Select(x => (x + 1) * 100)
                        .Select((commandId) =>
                            Task.Run(() =>
                            {
                                using (var connection = OpenDbConnection())
                                {
                                    try
                                    {
                                        var key = $"KEY_{commandId}";
                                        var command = new Command(connection, commandId);
                                        command.Execute(key);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Trapped escaped exception: {ex.Message}");
                                    }
                                }
                            })
                        );
                    Task.WhenAll(tasks).Wait();

                    Console.WriteLine("Results:");
                    using (var conn = OpenDbConnection())
                    {
                        var result = QueryZombieRecords(conn).ToArray();
                        foreach (var (table, value) in result)
                        {
                            Console.WriteLine($"\t[{table}] {value}");
                        }
                        if (result.Length == 3)
                        {
                            Console.WriteLine("\nHalting (detected failure mode)\n");
                            break;
                        }
                    }
                }
            }
        }

        private static void CleanZombieRecords(IDbConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    delete from [dbo].[TableA]
                    delete from [dbo].[TableB]
                ";
                command.ExecuteNonQuery();
            }
        }

        private static IEnumerable<(string Table, string Value)> QueryZombieRecords(IDbConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    select 'TableA', [Key] from [dbo].[TableA]
                    union all
                    select 'TableB', [Key] from [dbo].[TableB]
                ";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        yield return (reader.GetString(0), reader.GetString(1));
                    }
                }
            }
        }
    }

    public class Command
    {
        private IDbConnection _connection;
        private int _commandId;

        public Command(IDbConnection connection, int commandId)
        {
            _connection = connection;
            _commandId = commandId;
        }

        public void LogError(string message, Exception ex)
        {
            Console.WriteLine($"[{_commandId}] {message}\nError: {ex}");
        }

        public void LogInformation(string message)
        {
            Console.WriteLine($"[{_commandId}] {message}");
        }

        public void Execute(string keyValue)
        {
            // This sequence of commands (including the configuration of the Zombie database and the use of RepeatableRead isolation level)
            // replicates a real-world scenario using EF Core 2.2.4 on .NET Core 2.1
            //
            // Specifically it generates frequent deadlocks. We've fixed the deadlock issue, however
            // the handling of those deadlocks was very concerning..

            using (var transaction = _connection.BeginTransaction(IsolationLevel.RepeatableRead))
            {
                try
                {
                    LogInformation($"Transaction state before first command: {GetTransactionState(transaction)}");
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
                            SET NOCOUNT ON;
                            INSERT INTO [dbo].[TableB] ([Id], [Key], [Value])
                            VALUES (@p0, @p1, @p2);

                            -- here be deadlocks
                            SELECT [Sequence]
                            FROM [dbo].[TableB]
                            WHERE @@ROWCOUNT = 1 AND [Sequence] = scope_identity();
                        ";
                        AddParameter(command, "p0", DbType.Guid, Guid.NewGuid());
                        AddParameter(command, "p1", DbType.String, keyValue);
                        AddParameter(command, "p2", DbType.String, "the value string");

                        // WARN: ExecuteScalar does not consistently raise "deadlock victim" exceptions
                        var sequenceId = (long)command.ExecuteScalar();
                        LogInformation($"Read SequenceId {sequenceId}");

                        // ExecuteReader seems to raise them consistently?
                        //using (var reader = command.ExecuteReader())
                        //{
                        //    while (reader.Read())
                        //    {
                        //        var sequenceId = reader.GetInt64(0);
                        //        LogInformation($"Read SequenceId {sequenceId}");
                        //    }
                        //}
                    }
                    LogInformation($"Transaction state after first command: {GetTransactionState(transaction)}");

                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
                            SET NOCOUNT ON;
                            INSERT INTO [dbo].[TableA] ([Key], [Value])
                            VALUES (@p0, @p1);
                        ";
                        AddParameter(command, "p0", DbType.String, keyValue);
                        AddParameter(command, "p1", DbType.String, "the value string");
                        command.ExecuteNonQuery();
                    }
                    LogInformation($"Transaction state after second command: {GetTransactionState(transaction)}");

                    transaction.Commit();
                    LogInformation($"Transaction state after commit: {GetTransactionState(transaction)}");
                }
                catch (InvalidOperationException ex) when (ex.Message == "This SqlTransaction has completed; it is no longer usable.")
                {
                    LogError("Failed with problem exception", ex);
                    throw;
                }
                catch (Exception ex)
                {
                    LogError($"Failed with a normal exception", ex);
                    throw;
                }
            }
        }

        private static void AddParameter(IDbCommand command, string name, DbType type, object value)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.DbType = type;
            p.Value = value;
            command.Parameters.Add(p);
        }

        private static string GetTransactionState(IDbTransaction transaction)
        {
            var t2 = transaction.GetType() as System.Reflection.TypeInfo;
            var field2 = t2.DeclaredFields.Single(f => f.Name == "_internalTransaction");
            var _internalTransaction = field2.GetValue(transaction);
            if (_internalTransaction == null)
            {
                return "No transaction";
            }

            var t3 = _internalTransaction.GetType() as System.Reflection.TypeInfo;
            var field3 = t3.DeclaredFields.Single(f => f.Name == "_transactionState");
            var _transactionState = field3.GetValue(_internalTransaction);

            return _transactionState.ToString();
        }
    }
}
