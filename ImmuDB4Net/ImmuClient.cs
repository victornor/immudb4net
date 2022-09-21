/*
Copyright 2022 CodeNotary, Inc. All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

	http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace ImmuDB;

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using ImmuDB.Crypto;
using ImmuDB.Exceptions;
using ImmudbProxy;
using Org.BouncyCastle.Crypto;


/// <summary>
/// Class ImmuClient provides the awaitable API for accessing an ImmuDB server. If synchronous support is needed, use <see cref="ImmuClientSync" class />
/// </summary>
public partial class ImmuClient
{
    internal const string AUTH_HEADER = "authorization";

    private readonly AsymmetricKeyParameter? serverSigningKey;
    private readonly ImmuStateHolder stateHolder;
    private IConnection connection;
    private string currentDb = "defaultdb";
    private static LibraryWideSettings globalSettings = new LibraryWideSettings();
    private Session? activeSession;
    private TimeSpan heartbeatInterval;
    private ManualResetEvent? heartbeatCloseRequested;
    private Task? heartbeatTask;
    private ReleasedConnection releasedConnection = new ReleasedConnection();

    internal ImmuService.ImmuServiceClient Service { get { return Connection.Service; } }
    internal object connectionSync = new Object();
    internal IConnection Connection
    {
        get
        {
            lock (connectionSync)
            {
                return connection;
            }
        }

        set
        {
            lock (connectionSync)
            {
                connection = value;
            }
        }
    }
    internal IConnectionPool ConnectionPool { get; set; }
    internal ISessionManager SessionManager { get; set; }
    internal object sessionSync = new Object();
    internal int sessionSetupInProgress = 0;

    internal ManualResetEvent? heartbeatCalled;

    internal Session? ActiveSession
    {
        get
        {
            lock (sessionSync)
            {
                return activeSession;
            }
        }
        set
        {
            lock (sessionSync)
            {
                activeSession = value;
            }
        }
    }
    internal ImmuStateHolder StateHolder => stateHolder;

    /// <summary>
    /// Gets or sets the DeploymentInfoCheck flag. If enabled then a check of server authenticity is perform while establishing a new link with the ImmuDB server.
    /// </summary>
    /// <value>Default: true</value>
    public bool DeploymentInfoCheck { get; set; } = true;

    /// <summary>
    /// Gets or sets the length of time the <see cref="Shutdown" /> function is allowed to block before it completes.
    /// </summary>
    /// <value>Default: 2 sec</value>
    public TimeSpan ConnectionShutdownTimeout { get; set; }

    /// <summary>
    /// Gets or sets the length of time interval between periodic checks of idle connections to be closed. 
    /// </summary>
    /// <value></value>
    public TimeSpan IdleConnectionCheckInterval { get; internal set; }

    /// <summary>
    /// Gets the grpc address of the ImmuDB server
    /// </summary>
    /// <value></value>
    /// <remarks>
    /// This value is computed from the server url and server port arguments that come either from 
    /// <see cref="ImmuClientBuilder.Build()" /> or <see cref="ImmuClient.ImmuClient(string, int))" /> constructor
    /// </remarks>
    public string GrpcAddress { get; }

    /// <summary>
    /// Gets the <see cref="LibraryWideSettings" /> object with the process wide settings
    /// </summary>
    /// <value></value>
    public static LibraryWideSettings GlobalSettings
    {
        get
        {
            return globalSettings;
        }
    }

    /// <summary>
    /// Creates a new instance of <see cref="ImmuClientBuilder" /> factory object for <see cref="ImmuClient" /> instances
    /// </summary>
    /// <returns>A new instance of <see cref="ImmuClient" /></returns>
    public static ImmuClientBuilder NewBuilder()
    {
        return new ImmuClientBuilder();
    }

    /// <summary>
    /// This inner class keeps the global settings that are used across all <see cref="ImmuClient" /> instances
    /// </summary>
    public class LibraryWideSettings
    {
        /// <summary>
        /// Gets or sets the 
        /// </summary>
        /// <value></value>
        public int MaxConnectionsPerServer { get; set; }
        public TimeSpan TerminateIdleConnectionTimeout { get; set; }
        public TimeSpan IdleConnectionCheckInterval { get; set; }
        internal LibraryWideSettings()
        {
            //these are the default values
            MaxConnectionsPerServer = 2;
            TerminateIdleConnectionTimeout = TimeSpan.FromSeconds(60);
            IdleConnectionCheckInterval = TimeSpan.FromSeconds(6);
        }
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ImmuClient" />. It uses the default value of 'localhost:3322' as ImmuDB server address and port and 'defaultdb' as database name.
    /// </summary>
    /// <returns></returns>
    public ImmuClient() : this(NewBuilder())
    {

    }

    /// <summary>
    ///  Initializes a new instance of <see cref="ImmuClient" />. It uses the implicit 'defaultdb' value for the database name.
    /// </summary>
    /// <param name="serverUrl">The ImmuDB server address, ex: localhost or http://localhost </param>
    /// <param name="serverPort">The port where the ImmuDB server listens</param>
    public ImmuClient(string serverUrl, int serverPort)
        : this(NewBuilder().WithServerUrl(serverUrl).WithServerPort(serverPort))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ImmuClient" />
    /// </summary>
    /// <param name="serverUrl">The ImmuDB server address, ex: localhost or http://localhost </param>
    /// <param name="serverPort">The port where the ImmuDB server listens</param>
    /// <param name="database">The name of the ImmuDB server's database to use</param>
    public ImmuClient(string serverUrl, int serverPort, string database)
        : this(NewBuilder().WithServerUrl(serverUrl).WithServerPort(serverPort).WithDatabase(database))
    {
    }

    internal ImmuClient(ImmuClientBuilder builder)
    {
        ConnectionPool = builder.ConnectionPool;
        GrpcAddress = builder.GrpcAddress;
        connection = releasedConnection;
        SessionManager = builder.SessionManager;
        DeploymentInfoCheck = builder.DeploymentInfoCheck;
        serverSigningKey = builder.ServerSigningKey;
        stateHolder = builder.StateHolder;
        heartbeatInterval = builder.HeartbeatInterval;
        ConnectionShutdownTimeout = builder.ConnectionShutdownTimeout;
        stateHolder.DeploymentInfoCheck = builder.DeploymentInfoCheck;
        stateHolder.DeploymentKey = Utils.GenerateShortHash(GrpcAddress);
        stateHolder.DeploymentLabel = GrpcAddress;
    }

    /// <summary>
    /// Releases the resources used by the SDK objects. (ex: connection pool resources). As best practice, this method should be call just before the existing process ends.
    /// </summary>
    /// <returns></returns>
    public static async Task ReleaseSdkResources()
    {
        await RandomAssignConnectionPool.Instance.Shutdown();
    }

    private void StartHeartbeat()
    {
        heartbeatTask = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                if (heartbeatCloseRequested!.WaitOne(heartbeatInterval)) return;
                try
                {
                    await Service.KeepAliveAsync(new Empty(), Service.GetHeaders(ActiveSession));
                    heartbeatCalled?.Set();
                }
                catch (RpcException) { }
            }
        });
    }

    private void StopHeartbeat()
    {
        if (heartbeatTask == null)
        {
            return;
        }
        heartbeatCloseRequested?.Set();
        heartbeatTask.Wait();
        heartbeatCloseRequested?.Close();
        heartbeatCalled?.Close();
        heartbeatTask = null;
    }

    /// <summary>
    /// Opens a connection to the ImmuDB server or reuses one from the connection pool. It also initiates a session with the forementioned server.
    /// </summary>
    /// <param name="username">The username</param>
    /// <param name="password">The username's password</param>
    /// <param name="databaseName">The database to use</param>
    /// <returns></returns>
    public async Task Open(string username, string password, string databaseName)
    {
        try
        {
            using (ManualResetEvent mre = new ManualResetEvent(false))
            {
                while (true)
                {
                    if (Interlocked.Exchange(ref sessionSetupInProgress, 1) == 0)
                    {
                        break;
                    }
                    mre.WaitOne(2);
                }
            }
            if (ActiveSession != null)
            {
                throw new InvalidOperationException("please close the existing session before opening a new one");
            }
            Connection = ConnectionPool.Acquire(new ConnectionParameters
            {
                Address = GrpcAddress,
                ShutdownTimeout = ConnectionShutdownTimeout
            });            
            ActiveSession = await SessionManager.OpenSession(Connection, username, password, databaseName);
            heartbeatCloseRequested = new ManualResetEvent(false);
            heartbeatCalled = new ManualResetEvent(false);
            StartHeartbeat();
        }
        finally
        {
            Interlocked.Exchange(ref sessionSetupInProgress, 0);
        }
    }

    /// <summary>
    /// Releases to the connection pool the established connection and acquires a new one.
    /// </summary>
    /// <remarks>The active session not affected.</remarks>
    /// <returns></returns>
    public async Task Reconnect()
    {
        lock (connectionSync)
        {
            ConnectionPool.Release(connection);
            connection = ConnectionPool.Acquire(new ConnectionParameters
            {
                Address = GrpcAddress,
                ShutdownTimeout = ConnectionShutdownTimeout
            });
        }
        await Task.Yield();
    }

    /// <summary>
    /// Releases the established connection back to the connection pool and closes the active session
    /// </summary>
    /// <returns></returns>
    public async Task Close()
    {
        try
        {
            using (ManualResetEvent mre = new ManualResetEvent(false))
            {
                while (true)
                {
                    if (Interlocked.Exchange(ref sessionSetupInProgress, 1) == 0)
                    {
                        break;
                    }
                    mre.WaitOne(2);
                }
            }
            StopHeartbeat();
            await SessionManager.CloseSession(Connection, ActiveSession);
            ActiveSession = null;
            lock (connectionSync)
            {
                ConnectionPool.Release(connection);
                connection = releasedConnection;
            }
        }
        finally
        {
            Interlocked.Exchange(ref sessionSetupInProgress, 0);
        }
    }

    /// <summary>
    /// Gets the status of the connection
    /// </summary>
    /// <returns></returns>
    public bool IsClosed => Connection.Released;

    private void CheckSessionHasBeenOpened()
    {
        if (ActiveSession == null)
        {
            throw new ArgumentException("Session is null. Make sure you call Open beforehand.");
        }
    }

    private object stateSync = new Object();

    /// <summary>
    /// Gets the database state data and if not present then it is updated from the server
    /// </summary>
    /// <returns></returns>
    public ImmuState State
    {
        get
        {
            lock (stateSync)
            {
                ImmuState? state = stateHolder.GetState(ActiveSession, currentDb);
                if (state == null)
                {
                    state = CurrentState;
                    stateHolder.SetState(ActiveSession!, state);
                }
                else
                {
                    CheckSessionHasBeenOpened();
                }
                return state;
            }
        }
    }

    /// <summary>
    /// Get the current database state that exists on the server. It may throw a RuntimeException if server's state signature verification fails.
    /// </summary>
    /// <returns></returns>
    public ImmuState CurrentState
    {
        get
        {
            CheckSessionHasBeenOpened();
            ImmudbProxy.ImmutableState state = Service.CurrentState(new Empty(), Service.GetHeaders(ActiveSession));
            ImmuState immuState = ImmuState.ValueOf(state);
            if (!immuState.CheckSignature(serverSigningKey))
            {
                throw new VerificationException("State signature verification failed");
            }
            return immuState;
        }
    }

    //
    // ========== DATABASE ==========
    //

    /// <summary>
    /// Creates a new database
    /// </summary>
    /// <param name="database">The database name</param>
    /// <returns></returns>
    public async Task CreateDatabase(string database)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.CreateDatabaseRequest db = new ImmudbProxy.CreateDatabaseRequest
        {
            Name = database
        };

        await Service.CreateDatabaseV2Async(db, Service.GetHeaders(ActiveSession));
    }

    /// <summary>
    /// Changes the selected database
    /// </summary>
    /// <param name="database">The newly selected database. It must be an existing one.</param>
    /// <returns></returns>
    public async Task UseDatabase(string database)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.Database db = new ImmudbProxy.Database
        {
            DatabaseName = database
        };
        ImmudbProxy.UseDatabaseReply response = await Service.UseDatabaseAsync(db, Service.GetHeaders(ActiveSession));
        currentDb = database;
    }

    /// <summary>
    /// Gets the server's database list
    /// </summary>
    /// <returns>The server's database list</returns>
    public async Task<List<string>> Databases()
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.DatabaseListRequestV2 req = new ImmudbProxy.DatabaseListRequestV2();
        ImmudbProxy.DatabaseListResponseV2 res = await Service.DatabaseListV2Async(req, Service.GetHeaders(ActiveSession));
        List<string> list = new List<string>(res.Databases.Count);
        foreach (ImmudbProxy.DatabaseWithSettings db in res.Databases)
        {
            list.Add(db.Name);
        }
        return list;
    }

    //
    // ========== GET ==========
    //

    /// <summary>
    /// Retrieves the value for a specific key at a transaction ID.
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction id</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> GetAtTx(byte[] key, ulong tx)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.KeyRequest req = new ImmudbProxy.KeyRequest
        {
            Key = Utils.ToByteString(key),
            AtTx = tx
        };
        try
        {
            ImmudbProxy.Entry entry = await Service.GetAsync(req, Service.GetHeaders(ActiveSession));
            return Entry.ValueOf(entry);
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {
                throw new KeyNotFoundException();
            }

            throw e;
        }
    }

    /// <summary>
    /// Retrieves the value for a specific key and at a transaction ID.
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction id</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> Get(string key, ulong tx)
    {
        return await GetAtTx(Utils.ToByteArray(key), tx);
    }

    /// <summary>
    /// Retrieves the value for a key
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> Get(string key)
    {
        return await GetAtTx(Utils.ToByteArray(key), 0);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key
    /// </summary>
    /// <param name="keyReq">The lookup key, it is composed from the string key and the transaction id</param>
    /// <param name="state">The local state. One can get the local state by calling <see cref="State" /> property </param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGet(ImmudbProxy.KeyRequest keyReq, ImmuState state)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.VerifiableGetRequest vGetReq = new ImmudbProxy.VerifiableGetRequest()
        {
            KeyRequest = keyReq,
            ProveSinceTx = state.TxId
        };

        ImmudbProxy.VerifiableEntry vEntry;

        try
        {
            vEntry = await Service.VerifiableGetAsync(vGetReq, Service.GetHeaders(ActiveSession));
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {
                throw new KeyNotFoundException(string.Format("The key {0} was not found", keyReq.Key.ToStringUtf8()));
            }

            throw e;
        }

        ImmuDB.Crypto.InclusionProof inclusionProof = ImmuDB.Crypto.InclusionProof.ValueOf(vEntry.InclusionProof);
        ImmuDB.Crypto.DualProof dualProof = ImmuDB.Crypto.DualProof.ValueOf(vEntry.VerifiableTx.DualProof);

        byte[] eh;
        ulong sourceId, targetId;
        byte[] sourceAlh;
        byte[] targetAlh;

        Entry entry = Entry.ValueOf(vEntry.Entry);

        if (entry.ReferencedBy == null && !keyReq.Key.ToByteArray().SequenceEqual(entry.Key))
        {
            throw new VerificationException("Data is corrupted: entry does not belong to specified key");
        }

        if (entry.ReferencedBy != null && !keyReq.Key.ToByteArray().SequenceEqual(entry.ReferencedBy.Key))
        {
            throw new VerificationException("Data is corrupted: entry does not belong to specified key");
        }

        if (entry.Metadata != null && entry.Metadata.Deleted())
        {
            throw new VerificationException("Data is corrupted: entry is marked as deleted");
        }

        if (keyReq.AtTx != 0 && entry.Tx != keyReq.AtTx)
        {
            throw new VerificationException("Data is corrupted: entry does not belong to specified tx");
        }

        if (state.TxId <= entry.Tx)
        {
            byte[] digest = vEntry.VerifiableTx.DualProof.TargetTxHeader.EH.ToByteArray();
            eh = CryptoUtils.DigestFrom(digest);
            sourceId = state.TxId;
            sourceAlh = CryptoUtils.DigestFrom(state.TxHash);
            targetId = entry.Tx;
            targetAlh = dualProof.TargetTxHeader.Alh();
        }
        else
        {
            byte[] digest = vEntry.VerifiableTx.DualProof.SourceTxHeader.EH.ToByteArray();
            eh = CryptoUtils.DigestFrom(digest);
            sourceId = entry.Tx;
            sourceAlh = dualProof.SourceTxHeader.Alh();
            targetId = state.TxId;
            targetAlh = CryptoUtils.DigestFrom(state.TxHash);
        }

        byte[] kvDigest = entry.DigestFor(vEntry.VerifiableTx.Tx.Header.Version);
        if (!CryptoUtils.VerifyInclusion(inclusionProof, kvDigest, eh))
        {
            throw new VerificationException("Inclusion verification failed.");
        }

        if (state.TxId > 0)
        {
            if (!CryptoUtils.VerifyDualProof(
                    dualProof,
                    sourceId,
                    targetId,
                    sourceAlh,
                    targetAlh
            ))
            {
                throw new VerificationException("Dual proof verification failed.");
            }
        }

        ImmuState newState = new ImmuState(
                currentDb,
                targetId,
                targetAlh,
                (vEntry.VerifiableTx.Signature ?? ImmudbProxy.Signature.DefaultInstance).Signature_.ToByteArray());

        if (!newState.CheckSignature(serverSigningKey))
        {
            throw new VerificationException("State signature verification failed");
        }

        UpdateState(newState);
        return Entry.ValueOf(vEntry.Entry);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key. The assumed default TxID is 0
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGet(string key)
    {
        return await VerifiedGetAtTx(key, 0);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key. The assumed default TxID is 0
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGet(byte[] key)
    {
        return await VerifiedGetAtTx(key, 0);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetAtTx(string key, ulong tx)
    {
        return await VerifiedGetAtTx(Utils.ToByteArray(key), tx);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key at a transaction ID
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetAtTx(byte[] key, ulong tx)
    {
        ImmudbProxy.KeyRequest keyReq = new ImmudbProxy.KeyRequest()
        {
            Key = Utils.ToByteString(key),
            AtTx = tx
        };

        return await VerifiedGet(keyReq, State);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key since a transaction ID
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetSinceTx(string key, ulong tx)
    {
        return await VerifiedGetSinceTx(Utils.ToByteArray(key), tx);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key since a transaction ID
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetSinceTx(byte[] key, ulong tx)
    {
        ImmudbProxy.KeyRequest keyReq = new ImmudbProxy.KeyRequest()
        {
            Key = Utils.ToByteString(key),
            SinceTx = tx
        };

        return await VerifiedGet(keyReq, State);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key at a specific revision
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="rev">The revision number</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetAtRevision(string key, long rev)
    {
        return await VerifiedGetAtRevision(Utils.ToByteArray(key), rev);
    }

    /// <summary>
    /// Retrieves with authenticity check the value for a specific key at a specific revision
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="rev">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> VerifiedGetAtRevision(byte[] key, long rev)
    {
        ImmudbProxy.KeyRequest keyReq = new ImmudbProxy.KeyRequest()
        {
            Key = Utils.ToByteString(key),
            AtRevision = rev
        };

        return await VerifiedGet(keyReq, State);
    }

    /// <summary>
    /// Retrieves the value for a specific key since a transaction ID
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> GetSinceTx(string key, ulong tx)
    {
        return await GetSinceTx(Utils.ToByteArray(key), tx);
    }

    /// <summary>
    /// Retrieves the value for a specific key since a transaction ID
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="tx">The transaction ID</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> GetSinceTx(byte[] key, ulong tx)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.KeyRequest req = new ImmudbProxy.KeyRequest()
        {
            Key = Utils.ToByteString(key),
            SinceTx = tx
        };

        try
        {
            return Entry.ValueOf(await Service.GetAsync(req, Service.GetHeaders(ActiveSession)));
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {
                throw new KeyNotFoundException();
            }

            throw e;
        }
    }



    /// <summary>
    /// Retrieves the value for a specific key at a revision number
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="rev">The revision number</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> GetAtRevision(string key, long rev)
    {
        return await GetAtRevision(Utils.ToByteArray(key), rev);
    }

    /// <summary>
    /// Retrieves the value for a specific key at a revision number
    /// </summary>
    /// <param name="key">The lookup key</param>
    /// <param name="rev">The revision number</param>
    /// <returns>An <see ref="Entry"/> object. Most often the Value field is used.</returns>
    public async Task<Entry> GetAtRevision(byte[] key, long rev)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.KeyRequest req = new ImmudbProxy.KeyRequest()
        {
            Key = Utils.ToByteString(key),
            AtRevision = rev
        };

        try
        {
            return Entry.ValueOf(await Service.GetAsync(req, Service.GetHeaders(ActiveSession)));
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {
                throw new KeyNotFoundException();
            }

            throw e;
        }
    }

    /// <summary>
    /// Retrieves the values for the specified keys. This is a batch equivalent of Get.
    /// </summary>
    /// <param name="keys">The list of lookup keys</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> GetAll(List<string> keys)
    {
        CheckSessionHasBeenOpened();
        List<ByteString> keysBS = new List<ByteString>(keys.Count);

        foreach (string key in keys)
        {
            keysBS.Add(Utils.ToByteString(key));
        }

        ImmudbProxy.KeyListRequest req = new ImmudbProxy.KeyListRequest();
        req.Keys.AddRange(keysBS);

        ImmudbProxy.Entries entries = await Service.GetAllAsync(req, Service.GetHeaders(ActiveSession));
        List<Entry> result = new List<Entry>(entries.Entries_.Count);

        foreach (ImmudbProxy.Entry entry in entries.Entries_)
        {
            result.Add(Entry.ValueOf(entry));
        }

        return result;
    }

    //
    // ========== SCAN ==========
    //

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Optional, prefix for the keys</param>
    /// <param name="seekKey">Optional, initial key for the first entry in the iteration</param>
    /// <param name="endKey">Optional, end key for the scanning range</param>
    /// <param name="inclusiveSeek">Optional, default is true, specifies if initial key's value is included</param>
    /// <param name="inclusiveEnd">Optional, default is true, specifies if end key's value is included</param>
    /// <param name="limit">Optional, maximum number of of returned items</param>
    /// <param name="desc">Optional, specifies the sorting order, defaults to Desc</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(byte[] prefix, byte[] seekKey, byte[] endKey, bool inclusiveSeek, bool inclusiveEnd,
                            ulong limit, bool desc)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.ScanRequest req = new ImmudbProxy.ScanRequest()
        {
            Prefix = Utils.ToByteString(prefix),
            SeekKey = Utils.ToByteString(seekKey),
            EndKey = Utils.ToByteString(endKey),
            InclusiveSeek = inclusiveSeek,
            InclusiveEnd = inclusiveEnd,
            Limit = limit,
            Desc = desc
        };

        ImmudbProxy.Entries entries = await Service.ScanAsync(req, Service.GetHeaders(ActiveSession));
        return BuildList(entries);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(string prefix)
    {
        return await Scan(Utils.ToByteArray(prefix));
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(byte[] prefix)
    {
        return await Scan(prefix, 0, false);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies the sorting order</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(string prefix, ulong limit, bool desc)
    {
        return await Scan(Utils.ToByteArray(prefix), limit, desc);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies the sorting order</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(byte[] prefix, ulong limit, bool desc)
    {
        return await Scan(prefix, new byte[0], limit, desc);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="seekKey">Initial key for the first entry in the iteration</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies if the sorting order is of descending</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(string prefix, string seekKey, ulong limit, bool desc)
    {
        return await Scan(Utils.ToByteArray(prefix), Utils.ToByteArray(seekKey), limit, desc);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="seekKey">Initial key for the first entry in the iteration</param>
    /// <param name="endKey">End key for the scanning range</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies if the sorting order is of descending</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(string prefix, string seekKey, string endKey, ulong limit, bool desc)
    {
        return await Scan(Utils.ToByteArray(prefix), Utils.ToByteArray(seekKey), Utils.ToByteArray(endKey), limit, desc);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="seekKey">Initial key for the first entry in the iteration</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies if the sorting order is of descending</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(byte[] prefix, byte[] seekKey, ulong limit, bool desc)
    {
        return await Scan(prefix, seekKey, new byte[0], limit, desc);
    }

    /// <summary>
    /// Iterates over the key/values in the selected database and retrieves the values for the matching criteria
    /// </summary>
    /// <param name="prefix">Prefix of the keys</param>
    /// <param name="seekKey">Initial key for the first entry in the iteration</param>
    /// <param name="endKey">End key for the scanning range</param>
    /// <param name="limit">Maximum number of of returned items</param>
    /// <param name="desc">Specifies if the sorting order is of descending</param>
    /// <returns>A list of <see ref="Entry"/> objects.</returns>
    public async Task<List<Entry>> Scan(byte[] prefix, byte[] seekKey, byte[] endKey, ulong limit, bool desc)
    {
        return await Scan(prefix, seekKey, endKey, false, false, limit, desc);
    }

    //
    // ========== SET ==========
    //

    /// <summary>
    /// Adds a key/value pair.
    /// </summary>
    /// <param name="key">The key to be added</param>
    /// <param name="value">The value to be added</param>
    /// <returns>The transaction information</returns>
    public async Task<TxHeader> Set(byte[] key, byte[] value)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.KeyValue kv = new ImmudbProxy.KeyValue()
        {
            Key = Utils.ToByteString(key),
            Value = Utils.ToByteString(value)
        };

        ImmudbProxy.SetRequest req = new ImmudbProxy.SetRequest();
        req.KVs.Add(kv);

        ImmudbProxy.TxHeader txHdr = await Service.SetAsync(req, Service.GetHeaders(ActiveSession));

        if (txHdr.Nentries != 1)
        {
            throw new CorruptedDataException();
        }

        return TxHeader.ValueOf(txHdr);
    }

    /// <summary>
    /// Adds a key/value pair.
    /// </summary>
    /// <param name="key">The key to be added</param>
    /// <param name="value">The value to be added</param>
    /// <returns>The transaction information</returns>
    public async Task<TxHeader> Set(string key, byte[] value)
    {
        return await Set(Utils.ToByteArray(key), value);
    }

    /// <summary>
    /// Adds a key/value pair.
    /// </summary>
    /// <param name="key">The key to be added</param>
    /// <param name="value">The value to be added</param>
    /// <returns>The transaction information</returns>
    public async Task<TxHeader> Set(string key, string value)
    {
        return await Set(Utils.ToByteArray(key), Utils.ToByteArray(value));
    }

    /// <summary>
    /// Adds a list of key/value pairs
    /// </summary>
    /// <param name="kvList">The list of pairs to be added</param>
    /// <returns>The transaction information</returns>
    public async Task<TxHeader> SetAll(List<KVPair> kvList)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.SetRequest request = new ImmudbProxy.SetRequest();
        foreach (KVPair kv in kvList)
        {
            ImmudbProxy.KeyValue kvProxy = new ImmudbProxy.KeyValue();
            kvProxy.Key = Utils.ToByteString(kv.Key);
            kvProxy.Value = Utils.ToByteString(kv.Value);
            request.KVs.Add(kvProxy);
        }

        ImmudbProxy.TxHeader txHdr = await Service.SetAsync(request, Service.GetHeaders(ActiveSession));

        if (txHdr.Nentries != kvList.Count)
        {
            throw new CorruptedDataException();
        }

        return TxHeader.ValueOf(txHdr);
    }

    /// <summary>
    /// Adds a tag (reference) to a specific key/value element
    /// </summary>
    /// <param name="key"></param>
    /// <param name="referencedKey"></param>
    /// <param name="atTx"></param>
    /// <returns></returns>
    public async Task<TxHeader> SetReference(byte[] key, byte[] referencedKey, ulong atTx)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.ReferenceRequest req = new ImmudbProxy.ReferenceRequest()
        {
            Key = Utils.ToByteString(key),
            ReferencedKey = Utils.ToByteString(referencedKey),
            AtTx = atTx,
            BoundRef = atTx > 0
        };

        ImmudbProxy.TxHeader txHdr = await Service.SetReferenceAsync(req, Service.GetHeaders(ActiveSession));

        if (txHdr.Nentries != 1)
        {
            throw new CorruptedDataException();
        }

        return TxHeader.ValueOf(txHdr);
    }

    public async Task<TxHeader> SetReference(string key, string referencedKey, ulong atTx)
    {
        return await SetReference(
            Utils.ToByteArray(key),
            Utils.ToByteArray(referencedKey),
            atTx);
    }

    public async Task<TxHeader> SetReference(string key, string referencedKey)
    {
        return await SetReference(
            Utils.ToByteArray(key),
            Utils.ToByteArray(referencedKey),
            0);
    }

    public async Task<TxHeader> SetReference(byte[] key, byte[] referencedKey)
    {
        return await SetReference(key, referencedKey, 0);
    }

    public async Task<TxHeader> VerifiedSet(string key, byte[] value)
    {
        return await VerifiedSet(Utils.ToByteArray(key), value);
    }

    public async Task<TxHeader> VerifiedSet(string key, string value)
    {
        return await VerifiedSet(Utils.ToByteArray(key), Utils.ToByteArray(value));
    }

    public async Task<TxHeader> VerifiedSet(byte[] key, byte[] value)
    {
        CheckSessionHasBeenOpened();

        ImmuState state = State;
        ImmudbProxy.KeyValue kv = new ImmudbProxy.KeyValue()
        {
            Key = Utils.ToByteString(key),
            Value = Utils.ToByteString(value),
        };

        var setRequest = new ImmudbProxy.SetRequest();
        setRequest.KVs.Add(kv);
        ImmudbProxy.VerifiableSetRequest vSetReq = new ImmudbProxy.VerifiableSetRequest()
        {
            SetRequest = setRequest,
            ProveSinceTx = state.TxId
        };

        // using the awaitable VerifiableSetAsync is not ok here, because in the multithreading case it fails. Switched back to synchronous call in this case.

        var vtx = await Service.VerifiableSetAsync(vSetReq, Service.GetHeaders(ActiveSession));

        int ne = vtx.Tx.Header.Nentries;

        if (ne != 1 || vtx.Tx.Entries.Count != 1)
        {
            throw new VerificationException($"Got back {ne} entries (in tx metadata) instead of 1.");
        }

        Tx tx;
        try
        {
            tx = Tx.ValueOf(vtx.Tx);
        }
        catch (Exception e)
        {
            throw new VerificationException("Failed to extract the transaction.", e);
        }

        TxHeader txHeader = tx.Header;

        Entry entry = Entry.ValueOf(new ImmudbProxy.Entry()
        {
            Key = Utils.ToByteString(key),
            Value = Utils.ToByteString(value),
        });

        ImmuDB.Crypto.InclusionProof inclusionProof = tx.Proof(entry.getEncodedKey());

        if (!CryptoUtils.VerifyInclusion(inclusionProof, entry.DigestFor(txHeader.Version), txHeader.Eh))
        {
            throw new VerificationException("Data is corrupted (verify inclusion failed)");
        }

        ImmuState newState = VerifyDualProof(vtx, tx, state);

        if (!newState.CheckSignature(serverSigningKey))
        {
            throw new VerificationException("State signature verification failed");
        }

        UpdateState(newState);
        return TxHeader.ValueOf(vtx.Tx.Header);
    }

    private ImmuState VerifyDualProof(ImmudbProxy.VerifiableTx vtx, Tx tx, ImmuState state)
    {
        ulong sourceId = state.TxId;
        ulong targetId = tx.Header.Id;
        byte[] sourceAlh = CryptoUtils.DigestFrom(state.TxHash);
        byte[] targetAlh = tx.Header.Alh();

        if (state.TxId > 0)
        {
            if (!CryptoUtils.VerifyDualProof(
                    Crypto.DualProof.ValueOf(vtx.DualProof),
                    sourceId,
                    targetId,
                    sourceAlh,
                    targetAlh
            ))
            {
                throw new VerificationException("Data is corrupted (dual proof verification failed).");
            }
        }

        return new ImmuState(currentDb, targetId, targetAlh, (vtx.Signature ?? ImmudbProxy.Signature.DefaultInstance).Signature_.ToByteArray());
    }

    //
    // ========== Z ==========
    //

    public async Task<TxHeader> ZAdd(byte[] set, byte[] key, ulong atTx, double score)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.TxHeader txHdr = await Service.ZAddAsync(
                new ImmudbProxy.ZAddRequest()
                {
                    Set = Utils.ToByteString(set),
                    Key = Utils.ToByteString(key),
                    AtTx = atTx,
                    Score = score,
                    BoundRef = atTx > 0
                }, Service.GetHeaders(ActiveSession));

        if (txHdr.Nentries != 1)
        {
            throw new CorruptedDataException();
        }

        return TxHeader.ValueOf(txHdr);
    }

    public async Task<TxHeader> ZAdd(string set, string key, double score)
    {
        return await ZAdd(Utils.ToByteArray(set), Utils.ToByteArray(key), score);
    }

    public async Task<TxHeader> ZAdd(byte[] set, byte[] key, double score)
    {
        return await ZAdd(set, key, 0, score);
    }

    public async Task<TxHeader> VerifiedZAdd(byte[] set, byte[] key, ulong atTx, double score)
    {
        CheckSessionHasBeenOpened();

        ImmuState state = State;
        ImmudbProxy.ZAddRequest zAddReq = new ImmudbProxy.ZAddRequest()
        {
            Set = Utils.ToByteString(set),
            Key = Utils.ToByteString(key),
            AtTx = atTx,
            Score = score,
            BoundRef = atTx > 0
        };
        ImmudbProxy.VerifiableZAddRequest vZAddReq = new ImmudbProxy.VerifiableZAddRequest()
        {
            ZAddRequest = zAddReq,
            ProveSinceTx = state.TxId
        };

        ImmudbProxy.VerifiableTx vtx = await Service.VerifiableZAddAsync(vZAddReq, Service.GetHeaders(ActiveSession));

        if (vtx.Tx.Header.Nentries != 1)
        {
            throw new VerificationException("Data is corrupted.");
        }

        Tx tx;
        try
        {
            tx = Tx.ValueOf(vtx.Tx);
        }
        catch (Exception e)
        {
            throw new VerificationException("Failed to extract the transaction.", e);
        }

        TxHeader txHeader = tx.Header;

        ZEntry entry = ZEntry.ValueOf(new ImmudbProxy.ZEntry
        {
            Set = Utils.ToByteString(set),
            Key = Utils.ToByteString(key),
            AtTx = atTx,
            Score = score
        });

        Crypto.InclusionProof inclusionProof = tx.Proof(entry.getEncodedKey());

        if (!CryptoUtils.VerifyInclusion(inclusionProof, entry.DigestFor(txHeader.Version), txHeader.Eh))
        {
            throw new VerificationException("Data is corrupted (inclusion verification failed).");
        }

        if (!txHeader.Eh.SequenceEqual(CryptoUtils.DigestFrom(vtx.DualProof.TargetTxHeader.EH.ToByteArray())))
        {
            throw new VerificationException("Data is corrupted (different digests).");
        }

        ImmuState newState = VerifyDualProof(vtx, tx, state);

        if (!newState.CheckSignature(serverSigningKey))
        {
            throw new VerificationException("State signature verification failed");
        }
        UpdateState(newState);
        return TxHeader.ValueOf(vtx.Tx.Header);
    }

    public async Task<TxHeader> VerifiedZAdd(string set, string key, double score)
    {
        return await VerifiedZAdd(Utils.ToByteArray(set), Utils.ToByteArray(key), score);
    }

    public async Task<TxHeader> VerifiedZAdd(byte[] set, byte[] key, double score)
    {
        return await VerifiedZAdd(set, key, 0, score);
    }

    public async Task<TxHeader> VerifiedZAdd(string set, string key, ulong atTx, double score)
    {
        return await VerifiedZAdd(Utils.ToByteArray(set), Utils.ToByteArray(key), atTx, score);
    }

    public async Task<List<ZEntry>> ZScan(string set, ulong limit, bool reverse)
    {
        return await ZScan(Utils.ToByteArray(set), limit, reverse);
    }

    public async Task<List<ZEntry>> ZScan(byte[] set, ulong limit, bool reverse)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.ZScanRequest req = new ImmudbProxy.ZScanRequest()
        {
            Set = Utils.ToByteString(set),
            Limit = limit,
            Desc = reverse
        };

        ImmudbProxy.ZEntries zEntries = await Service.ZScanAsync(req, Service.GetHeaders(ActiveSession));
        return BuildList(zEntries);
    }

    //
    // ========== DELETE ==========
    //

    public async Task<TxHeader> Delete(string key)
    {
        return await Delete(Utils.ToByteArray(key));
    }

    public async Task<TxHeader> Delete(byte[] key)
    {
        CheckSessionHasBeenOpened();
        try
        {
            ImmudbProxy.DeleteKeysRequest req = new ImmudbProxy.DeleteKeysRequest()
            {
                Keys = { Utils.ToByteString(key) }
            };
            return TxHeader.ValueOf(await Service.DeleteAsync(req, Service.GetHeaders(ActiveSession)));
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {

                throw new KeyNotFoundException();
            }

            throw e;
        }
    }

    //
    // ========== TX ==========
    //

    public async Task<Tx> TxById(ulong txId)
    {
        CheckSessionHasBeenOpened();
        try
        {
            ImmudbProxy.Tx tx = await Service.TxByIdAsync(
                new ImmudbProxy.TxRequest()
                {
                    Tx = txId
                }, Service.GetHeaders(ActiveSession));
            return Tx.ValueOf(tx);
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("tx not found"))
            {
                throw new TxNotFoundException();
            }

            throw e;
        }
    }

    public async Task<Tx> VerifiedTxById(ulong txId)
    {
        CheckSessionHasBeenOpened();
        ImmuState state = State;
        ImmudbProxy.VerifiableTxRequest vTxReq = new ImmudbProxy.VerifiableTxRequest()
        {
            Tx = txId,
            ProveSinceTx = state.TxId
        };

        ImmudbProxy.VerifiableTx vtx;

        try
        {
            vtx = await Service.VerifiableTxByIdAsync(vTxReq, Service.GetHeaders(ActiveSession));
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("tx not found"))
            {
                throw new TxNotFoundException();
            }
            throw e;
        }

        Crypto.DualProof dualProof = Crypto.DualProof.ValueOf(vtx.DualProof);
        ulong sourceId;
        ulong targetId;
        byte[] sourceAlh;
        byte[] targetAlh;

        if (state.TxId <= txId)
        {
            sourceId = state.TxId;
            sourceAlh = CryptoUtils.DigestFrom(state.TxHash);
            targetId = txId;
            targetAlh = dualProof.TargetTxHeader.Alh();
        }
        else
        {
            sourceId = txId;
            sourceAlh = dualProof.SourceTxHeader.Alh();
            targetId = state.TxId;
            targetAlh = CryptoUtils.DigestFrom(state.TxHash);
        }

        if (state.TxId > 0)
        {
            if (!CryptoUtils.VerifyDualProof(
                    Crypto.DualProof.ValueOf(vtx.DualProof),
                    sourceId,
                    targetId,
                    sourceAlh,
                    targetAlh
            ))
            {
                throw new VerificationException("Data is corrupted (dual proof verification failed).");
            }
        }

        Tx tx;
        try
        {
            tx = Tx.ValueOf(vtx.Tx);
        }
        catch (Exception e)
        {
            throw new VerificationException("Failed to extract the transaction.", e);
        }

        ImmuState newState = new ImmuState(currentDb, targetId, targetAlh, (vtx.Signature ?? ImmudbProxy.Signature.DefaultInstance).Signature_.ToByteArray());

        if (!newState.CheckSignature(serverSigningKey))
        {
            throw new VerificationException("State signature verification failed");
        }
        UpdateState(newState);
        return tx;
    }

    private void UpdateState(ImmuState newState)
    {
        lock (stateSync)
        {
            stateHolder.SetState(ActiveSession!, newState);
        }
    }

    public async Task<List<Tx>> TxScan(ulong initialTxId)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.TxScanRequest req = new ImmudbProxy.TxScanRequest()
        {
            InitialTx = initialTxId
        };

        ImmudbProxy.TxList txList = await Service.TxScanAsync(req, Service.GetHeaders(ActiveSession));
        return buildList(txList);
    }

    public async Task<List<Tx>> TxScan(ulong initialTxId, uint limit, bool desc)
    {
        ImmudbProxy.TxScanRequest req = new ImmudbProxy.TxScanRequest()
        {
            InitialTx = initialTxId,
            Limit = limit,
            Desc = desc
        };
        ImmudbProxy.TxList txList = await Service.TxScanAsync(req, Service.GetHeaders(ActiveSession));
        return buildList(txList);
    }

    //
    // ========== HEALTH ==========
    //

    public async Task<bool> HealthCheck()
    {
        var healthResponse = await Service.HealthAsync(new Empty(), Service.GetHeaders(ActiveSession));
        return healthResponse.Status;
    }

    public bool IsConnected()
    {
        return !Connection.Released;
    }

    //
    // ========== USER MGMT ==========
    //

    public async Task<List<Iam.User>> ListUsers()
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.UserList userList = await Service.ListUsersAsync(new Empty(), Service.GetHeaders(ActiveSession));
        return userList.Users.ToList()
                .Select(u => new Iam.User(
                    u.User_.ToString(System.Text.Encoding.UTF8),
                    BuildPermissions(u.Permissions))
                {
                    Active = u.Active,
                    CreatedAt = u.Createdat,
                    CreatedBy = u.Createdby,
                }).ToList();
    }

    private List<Iam.Permission> BuildPermissions(RepeatedField<ImmudbProxy.Permission> permissionsList)
    {
        return permissionsList.ToList()
                .Select(p => (Iam.Permission)p.Permission_).ToList();
    }

    public async Task CreateUser(string user, string password, Iam.Permission permission, string database)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.CreateUserRequest createUserRequest = new ImmudbProxy.CreateUserRequest()
        {
            User = Utils.ToByteString(user),
            Password = Utils.ToByteString(password),
            Permission = (uint)permission,
            Database = database
        };

        await Service.CreateUserAsync(createUserRequest, Service.GetHeaders(ActiveSession));
    }

    public async Task ChangePassword(string user, string oldPassword, string newPassword)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.ChangePasswordRequest changePasswordRequest = new ImmudbProxy.ChangePasswordRequest()
        {
            User = Utils.ToByteString(user),
            OldPassword = Utils.ToByteString(oldPassword),
            NewPassword = Utils.ToByteString(newPassword),
        };

        await Service.ChangePasswordAsync(changePasswordRequest, Service.GetHeaders(ActiveSession));
    }

    //
    // ========== INDEX MGMT ==========
    //

    public async Task FlushIndex(float cleanupPercentage, bool synced)
    {
        CheckSessionHasBeenOpened();
        ImmudbProxy.FlushIndexRequest req = new ImmudbProxy.FlushIndexRequest()
        {
            CleanupPercentage = cleanupPercentage,
            Synced = synced
        };

        await Service.FlushIndexAsync(req, Service.GetHeaders(ActiveSession));
    }

    public async Task CompactIndex()
    {
        CheckSessionHasBeenOpened();
        await Service.CompactIndexAsync(new Empty(), Service.GetHeaders(ActiveSession));
    }

    //
    // ========== HISTORY ==========
    //

    public async Task<List<Entry>> History(string key, int limit, ulong offset, bool desc)
    {
        return await History(Utils.ToByteArray(key), limit, offset, desc);
    }

    public async Task<List<Entry>> History(byte[] key, int limit, ulong offset, bool desc)
    {
        CheckSessionHasBeenOpened();
        try
        {
            ImmudbProxy.Entries entries = await Service.HistoryAsync(new ImmudbProxy.HistoryRequest()
            {
                Key = Utils.ToByteString(key),
                Limit = limit,
                Offset = offset,
                Desc = desc
            }, Service.GetHeaders(ActiveSession));

            return BuildList(entries);
        }
        catch (RpcException e)
        {
            if (e.Message.Contains("key not found"))
            {
                throw new KeyNotFoundException();
            }

            throw e;
        }
    }

    private List<Entry> BuildList(ImmudbProxy.Entries entries)
    {
        List<Entry> result = new List<Entry>(entries.Entries_.Count);
        entries.Entries_.ToList().ForEach(entry => result.Add(Entry.ValueOf(entry)));
        return result;
    }

    private List<ZEntry> BuildList(ImmudbProxy.ZEntries entries)
    {
        List<ZEntry> result = new List<ZEntry>(entries.Entries.Count);
        entries.Entries.ToList()
                .ForEach(entry => result.Add(ZEntry.ValueOf(entry)));
        return result;
    }

    private List<Tx> buildList(ImmudbProxy.TxList txList)
    {
        List<Tx> result = new List<Tx>(txList.Txs.Count);
        txList.Txs.ToList().ForEach(tx =>
        {
            try
            {
                result.Add(Tx.ValueOf(tx));
            }
            catch (Exception e)
            {
                Console.WriteLine("An exception occurred into buildList: {0}", e);
            }
        });
        return result;
    }
}