﻿using Meadow.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Meadow.Foundation.Web.Maple
{
    /// <summary>
    /// A lightweight web server.
    /// </summary>
    public partial class MapleServer
    {
        public const int MAPLE_SERVER_BROADCASTPORT = 17756;
        public const int DefaultPort = 5417;

        private RequestMethodCache MethodCache { get; }

        private Dictionary<Type, IRequestHandler> _handlerCache = new Dictionary<Type, IRequestHandler>();
        private readonly HttpListener _httpListener = new HttpListener();
        private bool _isAdvertising;
        private bool _shouldAdvertise;

        private ErrorPageGenerator ErrorPageGenerator { get; }

        public Logger? Logger { get; }
        public IPAddress IPAddress { get; private set; }
        public int Port { get; private set; }

        /// <summary>
        /// Whether or not the server is listening for requests.
        /// </summary>
        public bool Running { get; protected set; } = false;

        /// <summary>
        /// Whether the server should operate on requests serially or in parallel.
        /// </summary>
        public RequestProcessMode ThreadingMode { get; protected set; }

        /// <summary>
        /// Whether or not the server should advertise it's name
        /// and IP via UDP for discovery.
        /// </summary>
        public bool Advertise
        {
            get => _shouldAdvertise;
            set
            {
                if (value && Running)
                {
                    StartUdpAdvertisement();
                }

                _shouldAdvertise = value;
            }
        }

        /// <summary>
        /// The interval, in milliseconds of how often to advertise.
        /// </summary>
        public int AdvertiseIntervalMs { get; set; } = 2000;

        /// <summary>
        /// The name of the device to advertise via UDP.
        /// </summary>
        public string DeviceName { get; set; } = "Meadow";

        public MapleServer(
            string ipAddress,
            int port = DefaultPort,
            bool advertise = false,
            RequestProcessMode processMode = RequestProcessMode.Serial,
            Logger logger = null)
            : this(IPAddress.Parse(ipAddress), port, advertise, processMode, logger)
        {
        }

        /// <summary>
        /// Creates a new MapleServer that listens on the specified IP Address
        /// and Port.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port">Defaults to 5417.</param>
        /// <param name="advertise">Whether or not to advertise via UDP.</param>
        /// <param name="processMode">Whether or not the server should respond to
        /// requests in parallel or serial. For Meadow, only Serial works
        /// reliably today.</param>
        public MapleServer(
            IPAddress ipAddress,
            int port = DefaultPort,
            bool advertise = false,
            RequestProcessMode processMode = RequestProcessMode.Serial,
            Logger? logger = null)
        {
            Logger = logger;
            MethodCache = new RequestMethodCache(Logger);
            ErrorPageGenerator = new ErrorPageGenerator();

            Create(ipAddress, port, advertise, processMode);
        }

        private void Create(IPAddress ipAddress,
            int port,
            bool advertise,
            RequestProcessMode processMode)
        {
            IPAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;

            Advertise = advertise;
            ThreadingMode = processMode;

            if (IPAddress.Equals(IPAddress.Any))
            {
                // because .NET is apparently too stupid to understand "bind to all"
                foreach (var ni in NetworkInterface
                    .GetAllNetworkInterfaces()
                    .SelectMany(i => i.GetIPProperties().UnicastAddresses))
                {
                    if (ni.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // for now, just use IPv4
                        Console.WriteLine($"Listening on http://{ni.Address}:{port}/");

                        //                        _httpListener.Prefixes.Add($"http://{ni.Address}:{port}/");
                    }
                }

                _httpListener.Prefixes.Add($"http://+:{port}/");
            }
            else
            {
                Console.WriteLine($"Listening on http://{IPAddress}:{port}/");

                _httpListener.Prefixes.Add($"http://{IPAddress}:{port}/");
            }

            LoadRequestHandlers();

            Initialize();
        }

        /// <summary>
        /// Initalize
        /// </summary>
        protected void Initialize()
        {
        }

        /// <summary>
        /// Starts listening to requests, and optionally advertises on UDP.
        /// </summary>
        public void Start()
        {
            try
            {
                _httpListener.Start();
            }
            catch (HttpListenerException e)
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // netsh http add urlacl url=http://+:5000/ user=Everyone
                    throw new Exception(
                        $"The server application needs elevated privileges or you must open permission on the URL (e.g. `netsh http add urlacl url=http://{IPAddress}:{Port}/ user=DOMAIN\\user`)");
                }

                throw;
            }

            if (Advertise)
            {
                StartUdpAdvertisement();
            }
            StartListeningToIncomingRequests();
        }

        /// <summary>
        /// Stops listening to requests and advertising (if running).
        /// </summary>
        public void Stop()
        {
            Running = false;
        }

        /// <summary>
        /// Begins advertising the server name and IP via UDP.
        /// </summary>
        protected void StartUdpAdvertisement()
        {
            Logger?.Debug($"StartUdpAdvertisement {_isAdvertising}");

            if (_isAdvertising) return;

            Task.Run(() =>
            {
                try
                {
                    _isAdvertising = true;

                    using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse("255.255.255.255"), MAPLE_SERVER_BROADCASTPORT);
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

                        string broadcastData = $"{DeviceName}::{IPAddress}";

                        while (Running && _shouldAdvertise)
                        {
                            Logger?.Info("UDP Broadcast: " + broadcastData + ", port: " + MAPLE_SERVER_BROADCASTPORT);
                            socket.SendTo(UTF8Encoding.UTF8.GetBytes(broadcastData), remoteEndPoint);

                            Thread.Sleep(AdvertiseIntervalMs);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger?.Error($"StartUdpAdvertisement error {e.Message}");
                }
                finally
                {
                    _isAdvertising = false;
                }
            });
        }

        /// <summary>
        /// Looks for IRequestHandlers and adds them to the `requestHandlers`
        /// collection for use later.
        /// </summary>
        protected void LoadRequestHandlers()
        {
            // Get classes that implement IRequestHandler
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var typesAdded = 0;

            // loop through each assembly in the app and all the classes in it
            foreach (var assembly in assemblies)
            {
                var types = assembly.GetTypes();
                foreach (var t in types)
                {
                    // if it inherits `IRequestHandler`, add it to the list
                    if (t.BaseType != null)
                    {
                        if (t.BaseType.GetInterfaces().Contains(typeof(IRequestHandler)))
                        {
                            MethodCache.AddType(t);
                            typesAdded++;
                        }
                    }
                }
            }

            if (typesAdded == 0)
            {
                Console.WriteLine("Warning: No Maple Server `IRequestHandler`s found. Server will not operate.");
            }
            else
            {
                Logger?.Info($"requestHandlers.Count: {typesAdded}");
            }
        }

        /// <summary>
        /// Starts a thread that listens to incoming Http requests and handles
        /// them. Note that the current implementation handles requests serially,
        /// rather than in parallel.
        /// </summary>
        /// <returns></returns>
        protected void StartListeningToIncomingRequests()
        {
            if (Running)
            {
                Logger?.Error("Already running.");
                return;
            }

            new Thread(RequestListenerProc).Start();
        }

        private async void RequestListenerProc()
        {
            Running = true;

            Logger?.Info("Starting up Maple HTTP Request listener.");

            while (Running)
            {
                HttpListenerContext context = null;

                try
                {
                    // wait for a request to come in
                    context = await _httpListener.GetContextAsync();
                    Logger?.Info($"Request received from {context.Request.RemoteEndPoint}");

                    // depending on our processing mode, process either
                    // synchronously, or spin off a thread and immediately
                    // process the next request (as it comes in)
                    switch (ThreadingMode)
                    {
                        case RequestProcessMode.Serial:
                            ProcessRequest(context).Wait();
                            context?.Response.Close();
                            break;
                        case RequestProcessMode.Parallel:
                            _ = Task.Run(async () =>
                            {
                                await ProcessRequest(context);
                                context?.Response.Close();
                            });
                            break;
                    }
                }
                catch (SocketException e)
                {
                    Logger?.Error("Socket Exception: " + e.ToString());
                }
                catch (Exception ex)
                {
                    Logger?.Error(ex.ToString());
                }
            }

            Logger?.Info("Maple HTTP Request listener stopped.");
            _httpListener.Close();
        }

        private IRequestHandler GetHandlerInstance(Type handlerType, out bool shouldDispose)
        {
            IRequestHandler target;
            shouldDispose = false;

            lock (_handlerCache)
            {
                if (_handlerCache.ContainsKey(handlerType))
                {
                    target = _handlerCache[handlerType];
                }
                else
                {
                    // instantiate the handler, set the context (which contains all the request info)
                    target = Activator.CreateInstance(handlerType) as IRequestHandler;

                    if (target.IsReusable)
                    {
                        // cache for later use
                        _handlerCache.Add(handlerType, target);
                    }
                    else
                    {
                        shouldDispose = true;
                    }
                }
            }

            return target;
        }

        protected async Task ProcessRequest(HttpListenerContext context)
        {
            string[] urlQuery = context.Request.RawUrl.Substring(1).Split('?');
            string[] urlParams = urlQuery[0].Split('/');
            string requestedMethodName = urlParams[0].ToLower();

            Logger?.Info("Received " + context.Request.HttpMethod + " " + context.Request.RawUrl + " - Invoking " + requestedMethodName);

            var handlerInfo = MethodCache.Match(context.Request.HttpMethod, context.Request.RawUrl, out object param);
            if (handlerInfo == null)
            {
                Logger?.Info("No handler found");
                await ErrorPageGenerator.SendErrorPage(context, 404, "Not Found");
                return;
            }
            else
            {
                var handlerInstance = GetHandlerInstance(handlerInfo.HandlerType, out bool shouldDispose);

                if (handlerInstance == null)
                {
                    Logger?.Info("Unable to get a handler instance");
                    await ErrorPageGenerator.SendErrorPage(context, 404, "Not Found");
                    return;
                }

                handlerInstance.Context = context;

                List<object> paramObjects = new List<object>();

                if (handlerInfo.Parameter != null)
                {
                    paramObjects.Add(param);
                }


                // does the method have a [FromBody] parameter?
                if (handlerInfo.Method.GetParameters().FirstOrDefault(p => p.CustomAttributes.Any(a => a.AttributeType.Equals(typeof(FromBodyAttribute)))) is { } p)
                {
                    using (var reader = new StreamReader(context.Request.InputStream))
                    {
                        var json = reader.ReadToEnd();

                        if (string.IsNullOrEmpty(json))
                        {
                            // check for empty body behavior
                            if (p.GetCustomAttributes(typeof(FromBodyAttribute), true).FirstOrDefault() as FromBodyAttribute is { } fb)
                            {
                                switch (fb.EmptyBodyBehavior)
                                {
                                    case EmptyBodyBehavior.Disallow:
                                        var msg = "Empty body disallowed";
                                        Logger?.Error(msg);
                                        await ErrorPageGenerator.SendErrorPage(context, 500, msg);
                                        break;
                                    default:
                                        paramObjects.Add(p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
                                        break;
                                }
                            }
                        }
                        else
                        {
                            var o = SimpleJson.SimpleJson.DeserializeObject(json, p.ParameterType);
                            paramObjects.Add(o);
                        }
                    }
                }

                var shouldContinueProcessing = true;

                // does the method have any [FromQuery] parameters?
                // dev note: this is fairly naieve.  If a handler had overloads of the same method with different numbers of params, we break.
                foreach (var parm in handlerInfo.Method.GetParameters().Where(p => p.CustomAttributes.Any(a => a.AttributeType.Equals(typeof(FromQueryAttribute)))))
                {
                    var value = handlerInstance.Context.Request.QueryString[parm.Name];

                    try
                    {
                        object? po;

                        if (value == null)
                        {
                            if (parm.HasDefaultValue)
                            {
                                po = parm.DefaultValue;
                            }
                            else
                            {
                                po = parm.ParameterType.IsValueType ? Activator.CreateInstance(parm.ParameterType) : null;
                            }
                        }
                        else
                        {
                            // first see if we can get a type converter for the param (i.e. value types)
                            var tc = TypeDescriptor.GetConverter(parm.ParameterType);
                            po = tc.ConvertFromString(value);
                        }

                        paramObjects.Add(po);
                    }
                    catch (ArgumentException)
                    {
                        // could not convert supplied parameter to method parameter type
                        if (parm.HasDefaultValue)
                        {
                            paramObjects.Add(parm.DefaultValue);
                        }
                        else
                        {
                            paramObjects.Add(parm.ParameterType.IsValueType ? Activator.CreateInstance(parm.ParameterType) : null);
                        }
                    }
                    catch (Exception ex)
                    {
                        await ErrorPageGenerator.SendErrorPage(context, 500, "Unable to find correct method for the supplied query parameters");
                        shouldContinueProcessing = false;
                        break;
                    }
                }

                if (shouldContinueProcessing)
                {
                    try
                    {
                        if (typeof(IActionResult).IsAssignableFrom(handlerInfo.Method.ReturnType))
                        {
                            var result = handlerInfo.Method.Invoke(handlerInstance, paramObjects.Count > 0 ? paramObjects.ToArray() : null) as IActionResult;
                            await result.ExecuteResultAsync(context);
                        }
                        else
                        {
                            handlerInfo.Method.Invoke(handlerInstance, paramObjects.Count > 0 ? paramObjects.ToArray() : null);
                        }

                        context.Response.Close();
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error(ex.Message);
                        await ErrorPageGenerator.SendErrorPage(context, 500, ex);
                    }
                }

                // if the handler is not reusable, clean up
                if (shouldDispose)
                {
                    Logger?.Debug("Disposing handler instance");
                    handlerInstance.Dispose();
                }

                Logger?.Debug("ProcessRequest complete");
            }
        }
    }
}