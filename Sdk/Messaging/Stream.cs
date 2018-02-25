﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using static Message.Types;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading;

namespace Sawtooh.Sdk.Messaging
{
    public class Stream
    {
        readonly string Address;

        readonly NetMQSocket Socket;
        readonly NetMQPoller Poller;

        readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> Futures;

        internal event EventHandler<TpProcessRequest> ProcessRequest;

        internal static SHA256 SHA256 = SHA256.Create();

        public Stream(string address)
        {
            Address = address;

            Socket = new DealerSocket();
            Socket.ReceiveReady += Receive;

            Poller = new NetMQPoller();
            Poller.Add(Socket);

            Futures = new ConcurrentDictionary<string, TaskCompletionSource<Message>>();
        }

        /// <summary>
        /// Generates a unique identifier
        /// </summary>
        /// <returns>The identifier.</returns>
        public static string GenerateId() => String.Concat(SHA256.ComputeHash(Guid.NewGuid().ToByteArray()).Select(x => x.ToString("x2")));

        void Receive(object _, NetMQSocketEventArgs e)
        {
            var message = new Message();
            message.MergeFrom(e.Socket.ReceiveFrameBytes());

            switch (message.MessageType)
            {
                case MessageType.PingRequest:
                    Socket.SendFrame(MessageExt.Encode(message, new PingResponse(), MessageType.PingResponse));
                    return;
                case MessageType.TpProcessRequest:
                    var processRequest = MessageExt.Decode<TpProcessRequest>(message);
                    ProcessRequest?.Invoke(this, processRequest);
                    return;
                default:
                    Debug.WriteLine($"Message of type {message.MessageType} received");
                    break;
            }

            if (Futures.TryGetValue(message.CorrelationId, out var source))
            {
                if (source.Task.Status == TaskStatus.RanToCompletion)
                {
                    Futures.TryRemove(message.CorrelationId, out var _);
                }
                else
                {
                    source.SetResult(message);
                }
            }
            else
            {
                Futures.TryAdd(message.CorrelationId, new TaskCompletionSource<Message>());
            }
        }

        public void Disconnect()
        {
            Socket.Disconnect(Address);
            Poller.StopAsync();
        }

        public Task<Message> Send(Message message, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<Message>();
            cancellationToken.Register(() => source.SetCanceled());

            Futures.TryAdd(message.CorrelationId, source);

            Socket.SendFrame(message.ToByteString().ToByteArray());

            return source.Task;
        }

        public void Connect()
        {
            Socket.Connect(Address);
            Poller.RunAsync();
        }
    }
}
