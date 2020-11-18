﻿using VpnHood.Messages;
using VpnHood.Server.Factory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VpnHood.Server
{
    public class SessionManager : IDisposable
    {
        private readonly ConcurrentDictionary<ulong, Session> Sessions = new ConcurrentDictionary<ulong, Session>();
        private readonly UdpClientFactory _udpClientFactory;
        private readonly ITracker _tracker;
        private const int SESSION_TimeoutSeconds = 60 * 5;
        private DateTime _lastCleanupTime = DateTime.MinValue;
        private IAccessServer AccessServer { get; }
        private ILogger Logger => Loggers.Logger.Current;

        public SessionManager(IAccessServer accessServer, UdpClientFactory udpClientFactory, ITracker tracker)
        {
            AccessServer = accessServer ?? throw new ArgumentNullException(nameof(accessServer));
            _udpClientFactory = udpClientFactory ?? throw new ArgumentNullException(nameof(udpClientFactory));
            _tracker = tracker;
        }

        public Session FindSessionByClientId(Guid clientId)
        {
            var session = Sessions.FirstOrDefault(x => x.Value.ClientId == clientId).Value;
            if (session == null)
                throw new KeyNotFoundException($"Invalid clientId! ClientId: {clientId}");

            return GetSessionById(session.SessionId);
        }

        public Session GetSessionById(ulong sessionId)
        {
            // find in session
            if (!Sessions.TryGetValue(sessionId, out Session session))
                throw new KeyNotFoundException($"Invalid SessionId, SessionId: {sessionId}");

            // check session status
            if (!session.IsDisposed)
                session.UpdateStatus();

            bool accessError = session.AccessController.ResponseCode != ResponseCode.Ok;
            if (session.IsDisposed)
            {
                throw new SessionException(
                    accessUsage: session.AccessController.AccessUsage,
                    responseCode: accessError ? session.AccessController.ResponseCode : ResponseCode.SessionClosed,
                    suppressedBy: session.SuppressedBy,
                    message: accessError ? session.AccessController.Access.Message : "Session has been closed"
                    );
            }

            return session;


        }

        public async Task<Session> CreateSession(HelloRequest helloRequest, IPAddress clientIp)
        {
            // create the identity
            var clientIdentity = new ClientIdentity()
            {
                ClientId = helloRequest.ClientId,
                ClientIp = clientIp.ToString(),
                TokenId = helloRequest.TokenId,
                UserToken = helloRequest.UserToken
            };

            // validate the token
            Logger.Log(LogLevel.Trace, $"Validating the request. TokenId: {Util.FormatId(clientIdentity.TokenId)}");
            var accessController = await GetValidatedAccess(clientIdentity, helloRequest.EncryptedClientId);

            // cleanup old timeout sessions
            RemoveTimeoutSessions();

            // first: suppress a session of same client if maxClient is exceeded
            var oldSession = Sessions.FirstOrDefault(x => !x.Value.IsDisposed && x.Value.ClientId == clientIdentity.ClientId).Value;

            // second: suppress a session of other with same accessId if MaxClientCount is exceeded. MaxClientCount zero means unlimited 
            if (oldSession == null && accessController.Access.MaxClientCount > 0)
            {
                var otherSessions = Sessions.Where(x => !x.Value.IsDisposed && x.Value.AccessController.Access.AccessId == accessController.Access.AccessId)
                    .OrderBy(x => x.Value.CreatedTime).ToArray();
                if (otherSessions.Length >= accessController.Access.MaxClientCount)
                    oldSession = otherSessions[0].Value;
            }

            if (oldSession != null)
            {
                Logger.LogInformation($"Suppressing other session. SuppressedClientId: {Util.FormatId(oldSession.ClientId)}, SuppressedSessionId: {Util.FormatId(oldSession.SessionId)}");
                oldSession.SuppressedByClientId = clientIdentity.ClientId;
                oldSession.Dispose();
            }

            // create new session
            var session = new Session(clientIdentity, accessController, _udpClientFactory)
            {
                SuppressedToClientId = oldSession?.ClientId
            };
            Sessions.TryAdd(session.SessionId, session);
            _tracker?.TrackEvent("Usage", "SessionCreated").GetAwaiter();
            Logger.Log(LogLevel.Information, $"New session has been created. SessionId: {Util.FormatId(session.SessionId)}");

            return session;
        }

        private async Task<AccessController> GetValidatedAccess(ClientIdentity clientIdentity, byte[] encryptedClientId)
        {
            // get access
            var access = await AccessServer.GetAccess(clientIdentity);
            if (access == null)
                throw new Exception($"Could not find the tokenId! {clientIdentity.TokenId}, ClientId: {clientIdentity.ClientId}");

            // Validate token by shared secret
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Key = access.Secret;
            aes.IV = new byte[access.Secret.Length];
            aes.Padding = PaddingMode.None;
            using var cryptor = aes.CreateEncryptor();
            var ecid = cryptor.TransformFinalBlock(clientIdentity.ClientId.ToByteArray(), 0, clientIdentity.ClientId.ToByteArray().Length);
            if (!Enumerable.SequenceEqual(ecid, encryptedClientId))
                throw new Exception($"The request does not have a valid signature for requested token! {clientIdentity.TokenId}, ClientId: {clientIdentity.ClientId}");

            // find AccessController or Create
            var accessController =
                Sessions.FirstOrDefault(x => x.Value.AccessController.Access.AccessId == access.AccessId).Value?.AccessController
                ?? new AccessController(clientIdentity, AccessServer, access);

            accessController.Access = access; // update access control
            accessController.UpdateStatusCode();

            // check access
            if (accessController.Access.StatusCode != AccessStatusCode.Ok)
                throw new SessionException(
                    accessUsage: accessController.AccessUsage,
                    responseCode: accessController.ResponseCode,
                    suppressedBy: SuppressType.None,
                    message: accessController.Access.Message
                    );

            return accessController;
        }

        private void RemoveTimeoutSessions()
        {
            if ((DateTime.Now - _lastCleanupTime).TotalSeconds < SESSION_TimeoutSeconds)
                return;
            _lastCleanupTime = DateTime.Now;

            // removing timeout Sessions
            Logger.Log(LogLevel.Trace, $"Removing timeout sessions...");
            foreach (var item in Sessions.Where(x => x.Value.IsDisposed && (DateTime.Now - x.Value.DisposeTime.Value).TotalSeconds >= SESSION_TimeoutSeconds))
            {
                Sessions.Remove(item.Key, out _);
                item.Value.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var session in Sessions.Values)
                session.Dispose();
        }
    }
}