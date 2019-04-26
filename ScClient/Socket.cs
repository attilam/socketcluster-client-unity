﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using LitJson;
using SuperSocket.ClientEngine;
using SuperSocket.ClientEngine.Proxy;
using WebSocket4Net;
using UnityEngine;

namespace ScClient
{
    public class Socket : Emitter, IDisposable
    {
        public WebSocket _socket;
        public string id;
        private long _counter;
        private string _authToken;
        private List<Channel> _channels;
        private IReconnectStrategy _strategy;
        private Dictionary<long?, object[]> acks;
        private IBasicListener _listener;
        private Thread reconnectionThread;

        public Socket(string url)
        {
            _socket = new WebSocket(url);
            _counter = 0;
            _strategy = null;
            _channels = new List<Channel>();
            acks = new Dictionary<long?, object[]>();

            // hook in all the event handling
            _socket.Opened += OnWebsocketConnected;
            _socket.Error += OnWebsocketError;
            _socket.Closed += OnWebsocketClosed;
            _socket.MessageReceived += OnWebsocketMessageReceived;
            _socket.DataReceived += OnWebsocketDataReceived;
        }

        public void Dispose()
		{
            _socket.Opened -= OnWebsocketConnected;
            _socket.Error -= OnWebsocketError;
            _socket.Closed -= OnWebsocketClosed;
            _socket.MessageReceived -= OnWebsocketMessageReceived;
            _socket.DataReceived -= OnWebsocketDataReceived;
			_socket.Dispose();
            _socket = null;
		}

        public void SetReconnectStrategy(IReconnectStrategy strategy)
        {
            _strategy = strategy;
        }

        public void SetProxy(string host, int port)
        {
            var proxy = new HttpConnectProxy(new IPEndPoint(IPAddress.Parse(host), port));
            _socket.Proxy = (SuperSocket.ClientEngine.IProxyConnector) proxy;
        }

        public void SetSslCertVerification(bool value)
        {
            _socket.Security.AllowUnstrustedCertificate = value;
        }


        public Channel CreateChannel(string name)
        {
            var channel = new Channel(this, name);
            _channels.Add(channel);
            return channel;
        }

        public List<Channel> GetChannels()
        {
            return _channels;
        }

        public Channel GetChannelByName(string name)
        {
            return _channels.FirstOrDefault(channel => channel.GetChannelName().Equals(name));
        }

        private void SubscribeChannels()
        {
            foreach (var channel in _channels)
            {
                channel.Subscribe();
            }
        }

        public void SetAuthToken(string token)
        {
            _authToken = token;
        }

        public void SetListener(IBasicListener listener)
        {
            _listener = listener;
        }

        private void OnWebsocketConnected(object sender, EventArgs e)
        {
            _counter = 0;
            _strategy?.SetAttemptsMade(0);

            var authobject = new Dictionary<string, object>
            {
                {"event", "#handshake"},
                {"data", new Dictionary<string, object> {{"authToken", _authToken}}},
                {"cid", Interlocked.Increment(ref _counter)}
            };
            var json = JsonMapper.ToJson(authobject);

            ((WebSocket) sender).Send(json);

            _listener.OnConnected(this);
        }

        private void OnWebsocketError(object sender, ErrorEventArgs e)
        {
            _listener.OnConnectError(this, e);
        }

        private void OnWebsocketClosed(object sender, EventArgs e)
        {
            _listener.OnDisconnected(this);
            if (_strategy != null && !_strategy.AreAttemptsComplete())
            {
                _strategy.ProcessValues();
                reconnectionThread = new Thread(Reconnect);
                reconnectionThread.Start(_strategy.GetReconnectInterval());
            }
        }

        private void OnWebsocketMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (e.Message == "#1")
                {
                    _socket.Send("#2");
                }
                else
                {
    //                Console.WriteLine("Message received :: "+e.Message);

                    var dict = JsonMapper.ToObject(e.Message);

                    var dataobject = dict.ContainsKey("data") ? dict["data"] : null;
                    var rid = dict.ContainsKey("rid") ? (int?)dict["rid"] : null;
                    var cid = dict.ContainsKey("cid") ? (int?)dict["cid"] : null;
                    var Event = dict.ContainsKey("event") ? (string)dict["event"] : null;
                    var errorobject = dict.ContainsKey("error") ? dict["error"] : null;

                    //                Console.WriteLine("data is "+e.Message);
                    //                Console.WriteLine("data is "+dataobject +" rid is "+rid+" cid is "+cid+" event is "+Event);

                    switch (Parser.Parse(dataobject, rid, cid, Event))
                    {
                        case Parser.MessageType.Isauthenticated:
    //                        Console.WriteLine("IS authenticated got called");
                            id = (string) dataobject["id"];
                            _listener.OnAuthentication(this, (bool) ((JsonData) dataobject)["isAuthenticated"]);
                            SubscribeChannels();
                            break;
                        case Parser.MessageType.Publish:
                            HandlePublish((string) dataobject["channel"],
                                dataobject["data"]);
    //                        Console.WriteLine("Publish got called");
                            break;
                        case Parser.MessageType.Removetoken:
                            SetAuthToken(null);
    //                        Console.WriteLine("Removetoken got called");
                            break;
                        case Parser.MessageType.Settoken:
                            _listener.OnSetAuthToken((string) dataobject["token"], this);
    //                        Console.WriteLine("Set token got called");
                            break;
                        case Parser.MessageType.Event:

                            if (HasEventAck(Event))
                            {
                                HandleEmitAck(Event, dataobject, Ack(cid));
                            }
                            else
                            {
                                HandleEmit(Event, dataobject);
                            }

                            break;
                        case Parser.MessageType.Ackreceive:

    //                        Console.WriteLine("Ack receive got called");
                            if (acks.ContainsKey(rid))
                            {
                                var Object = acks[rid];
                                acks.Remove(rid);
                                if (Object != null)
                                {
                                    var fn = (Ackcall) Object[1];
                                    if (fn != null)
                                    {
                                        fn((string) Object[0], errorobject,
                                            dataobject);
                                    }
                                    else
                                    {
                                        Console.WriteLine("Ack function is null");
                                    }
                                }
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }


        private void OnWebsocketDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.WriteLine("Data received ");
        }

        public void Connect()
        {
            _socket.Open();
        }

        private void Reconnect(object reconnectIntervalObject)
        {
            int reconnectInterval = (int)reconnectIntervalObject;
            Thread.Sleep(reconnectInterval);
            if (_socket == null)
            {
                return;
            }
            Connect();
        }

        public void Disconnect()
        {
            _socket.Close();
        }

        private Ackcall Ack(long? cid)
        {
            return (name, error, data) =>
            {
                Dictionary<string, object> dataObject =
                    new Dictionary<string, object> {{"error", error}, {"data", data}, {"rid", cid}};
                var json = JsonMapper.ToJson(dataObject);
                _socket.Send(json);
            };
        }


        public Socket Emit(string Event, object Object)
        {
//            Console.WriteLine("Emit got called");
            Dictionary<string, object>
                eventObject = new Dictionary<string, object> {{"event", Event}, {"data", Object}};
            var json = JsonMapper.ToJson(eventObject);
            _socket.Send(json);
            return this;
        }

        public Socket Emit(string Event, object Object, Ackcall ack)
        {
            long count = Interlocked.Increment(ref _counter);
            Dictionary<string, object> eventObject =
                new Dictionary<string, object> {{"event", Event}, {"data", Object}, {"cid", count}};
            acks.Add(count, GetAckObject(Event, ack));
            var json = JsonMapper.ToJson(eventObject);
            _socket.Send(json);
            return this;
        }

        public Socket Subscribe(string channel)
        {
            Dictionary<string, object> subscribeObject = new Dictionary<string, object>
            {
                {"event", "#subscribe"},
                {"data", new Dictionary<string, string> {{"channel", channel}}},
                {"cid", Interlocked.Increment(ref _counter)}
            };
            var json = JsonMapper.ToJson(subscribeObject);
            _socket.Send(json);
            return this;
        }

        public Socket Subscribe(string channel, Ackcall ack)
        {
            long count = Interlocked.Increment(ref _counter);
            Dictionary<string, object> subscribeObject = new Dictionary<string, object>
            {
                {"event", "#subscribe"},
                {"data", new Dictionary<string, string>() {{"channel", channel}}},
                {"cid", count}
            };
            acks.Add(count, GetAckObject(channel, ack));
            var json = JsonMapper.ToJson(subscribeObject);
            _socket.Send(json);
            return this;
        }

        public Socket Unsubscribe(string channel)
        {
            Dictionary<string, object> subscribeObject = new Dictionary<string, object>
            {
                {"event", "#unsubscribe"},
                {"data", channel},
                {"cid", Interlocked.Increment(ref _counter)}
            };
            var json = JsonMapper.ToJson(subscribeObject);
            _socket.Send(json);
            return this;
        }

        public Socket Unsubscribe(string channel, Ackcall ack)
        {
            long count = Interlocked.Increment(ref _counter);
            Dictionary<string, object> subscribeObject =
                new Dictionary<string, object> {{"event", "#unsubscribe"}, {"data", channel}, {"cid", count}};
            acks.Add(count, GetAckObject(channel, ack));
            var json = JsonMapper.ToJson(subscribeObject);
            _socket.Send(json);
            return this;
        }

        public Socket Publish(string channel, object data)
        {
            Dictionary<string, object> publishObject = new Dictionary<string, object>
            {
                {"event", "#publish"},
                {"data", new Dictionary<string, object> {{"channel", channel}, {"data", data}}},
                {"cid", Interlocked.Increment(ref _counter)}
            };
            var json = JsonMapper.ToJson(publishObject);
            _socket.Send(json);
            return this;
        }

        public Socket Publish(string channel, object data, Ackcall ack)
        {
            long count = Interlocked.Increment(ref _counter);
            Dictionary<string, object> publishObject = new Dictionary<string, object>
            {
                {"event", "#publish"},
                {"data", new Dictionary<string, object> {{"channel", channel}, {"data", data}}},
                {"cid", count}
            };
            acks.Add(count, GetAckObject(channel, ack));
            var json = JsonMapper.ToJson(publishObject);
            _socket.Send(json);
            return this;
        }


        private object[] GetAckObject(string Event, Ackcall ack)
        {
            object[] Object = {Event, ack};
            return Object;
        }

		public class Channel
        {
            private readonly string _channelname;
            private readonly Socket _socket;

            public Channel(Socket socket, string channelName)
            {
                this._socket = socket;
                _channelname = channelName;
            }

            public Channel Subscribe()
            {
                _socket.Subscribe(_channelname);
                return this;
            }

            public Channel Subscribe(Ackcall ack)
            {
                _socket.Subscribe(_channelname, ack);
                return this;
            }

            public void OnMessage(Listener listener)
            {
                _socket.OnSubscribe(_channelname, listener);
            }

            public void Publish(object data)
            {
                _socket.Publish(_channelname, data);
            }

            public void Publish(object data, Ackcall ack)
            {
                _socket.Publish(_channelname, data, ack);
            }

            public void Unsubscribe()
            {
                _socket.Unsubscribe(_channelname);
                _socket._channels.Remove(this);
            }

            public void Unsubscribe(Ackcall ack)
            {
                _socket.Unsubscribe(_channelname, ack);
                _socket._channels.Remove(this);
            }

            public string GetChannelName()
            {
                return _channelname;
            }
        }
    }
}