using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using H.Socket.IO;
using H.Engine.IO;
using H.WebSockets;
using Microsoft.Xna.Framework;
using TAS;
using TAS.Input;
using Celeste.Mod.Dataline;

public class ControlMessage
{
    public bool? newState;
}

namespace Celeste.Mod.Dataline
{
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
            Log("Dataline Dev revision tas data");
            Connect();
            orig(self);
        }

        public void Log(string msg)
        {
            Logger.Log(LogLevel.Info, "DatalineModule", msg);
        }
    }
}

#pragma warning disable CS0626 // orig_ method is marked external and has no attributes on it.
namespace TAS
{
    // The patch_ class is in the same namespace as the original class.
    // This can be bypassed by placing it anywhere else and using [MonoModPatch("global::Celeste.Player")]

    // Visibility defaults to "internal", which hides your patch from runtime mods.
    // If you want to "expose" new members to runtime mods, create extension methods in a public static class PlayerExt
    class patch_InputHelper
    { // : Player lets us reuse any of its visible members without redefining them.
        // MonoMod creates a copy of the original method, called orig_Added.
        public static extern void orig_FeedInputs(InputFrame input);
        public static void patch_FeedInputs(InputFrame input)
        {
            DatalineModule.Instance.Log("input fed " + input);
            orig_FeedInputs(input);
        }
    }
}