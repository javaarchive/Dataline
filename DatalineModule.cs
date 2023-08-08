using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using H.Socket.IO;
using Microsoft.Xna.Framework;

public class ControlMessage
{
    public bool? newState;
}

namespace Celeste.Mod.Dataline {
    public class DatalineModule : EverestModule
    {
        public static DatalineModule Instance { get; private set; }

        public override Type SettingsType => typeof(DatalineModuleSettings);
        public static DatalineModuleSettings Settings => (DatalineModuleSettings)Instance._Settings;

        public override Type SessionType => typeof(DatalineModuleSession);
        public static DatalineModuleSession Session => (DatalineModuleSession)Instance._Session;

        public bool blockNativeInput = false;

        public List<Microsoft.Xna.Framework.Input.Keys> virtualKeyPresses = new List<Microsoft.Xna.Framework.Input.Keys>();

        public SocketIoClient socket;
        public DatalineModule()
        {
            Instance = this;
#if DEBUG
            // debug builds use verbose logging
            Logger.SetLogLevel(nameof(DatalineModule), LogLevel.Verbose);
#else
            // release builds use info logging to reduce spam in log files
            Logger.SetLogLevel(nameof(DatalineModule), LogLevel.Info);
#endif
        }

        public string GetServerEndpoint()
        {
            return Environment.GetEnvironmentVariable("CONTROL_SERVER_URL") ?? "ws://127.0.0.1:3137";
        }

        public override void Load()
        {
            On.Celeste.Celeste.Initialize += modInitalize;
        }

        public override void Unload()
        {
            // TODO: unapply any hooks applied in Load()
            On.Celeste.Celeste.Initialize -= modInitalize;
        }

        public void RegisterListeners()
        {
            socket.On("identify", response =>
            {
                Logger.Log(LogLevel.Info, "DatalineModule", "Sending Identity");
                socket.Emit("identifier", Process.GetCurrentProcess().Id).GetAwaiter().GetResult(); ;
            });

            socket.On<ControlMessage>("setBorderless", msg =>
            {
                Celeste.Instance.Window.IsBorderlessEXT = msg.newState ?? false;
            });

            socket.On<ControlMessage>("setNativeInput", msg =>
            {
                blockNativeInput = msg.newState ?? false;
            });

        }

        public void Connect()
        {
            SocketIoClient client = new SocketIoClient();
            socket = client;
            Logger.Log(LogLevel.Info, "DatalineModule", "Constructed socket..");
            RegisterListeners();
            Logger.Log(LogLevel.Info, "DatalineModule", "Running socket connect...");
            bool status = socket.ConnectAsync(new Uri(GetServerEndpoint())).GetAwaiter().GetResult();
            Logger.Log(LogLevel.Info, "DatalineModule", "Connect task finished " + status);
            Logger.Log(LogLevel.Info, "DatalineModule", "Socket ok, waiting for hello/identify!");
        }


        private void modInitalize(On.Celeste.Celeste.orig_Initialize orig, Celeste self)
        {
            Connect();
        }
    }
}