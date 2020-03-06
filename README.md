
This reduced test-case reproduces an issue seen in production using EF Core 2.2.4 on .NET Core 2.1.

The sequence of inserts and selects, the  configuration of the Zombie database and the use of RepeatableRead
isolation level replicate the real-world scenario, though not all these factors may be required.

# Instructions

1.  Create a new database using the `ZombieDb.sql` script
2.  Run the test program:

    For .NET Core 3.1 with Microsoft.Data.SqlClient:
    ```
    > dotnet run -p .\ZombieTester.NET31.csproj
    ```

    For .NET Core 2.1 with System.Data.SqlClient:
    ```
    > dotnet run -p .\ZombieTester.NET21.csproj
    ```

3.  Press any key to start testing for the error case. The test will run until it reproduces the issue,
    or times out in one minute.

# Explanation

Occasionally SqlClient fails to raise a SqlException when a command is rolled back as a deadlock victim.
The transaction is correctly marked as `Aborted`, however subsequent commands issued with that transaction
fail to check the transaction status and so end up being silently executed outside of a transaction.

EF Core is sensitive to this issue a it also does not bother to check the state of the transaction in between
commands - it's expecting an exception if anything goes wrong. So:

1.  EF Core as a bunch of changes queued up: TableA, TableB
2.  SaveChanges called
3.  Execute command "insert into TableB values ...; select Sequence from TableB;"
4.  Deadlock
    - Chosen as victim and rolled back
    - Transaction marked as "Aborted"
    - No deadlock exception raised...
5.  Execute command "insert into TableA values ...;"
6.  Mark all pending changes as committed
7.  SaveChanges ends

In the normal case step number 4 will fail with a SqlException indicating that the command was chosen as a
deadlock victim, allowing the user code the opportunity to abandon the transaction and retry the SaveChanges
operation.

Instead, when we finally call `transaction.Complete()` to commit the changes an exception is raised as the
transaction is already in a completed state (Aborted). Unfortunately it is at this moment impossible to roll
back the committed changes.