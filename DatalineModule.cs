using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using H.Socket.IO;
using H.Engine.IO;
using H.WebSockets;
using Microsoft.Xna.Framework;
using TAS;
using TAS.Input;
using Celeste.Mod.Dataline;
using MonoMod.RuntimeDetour;
using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using TAS.Input.Commands;
using System.Threading;

public class ControlMessage
{
    public bool? newState;
    public int? newValue;
    public string? command;
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

        public Detour feedInputDetour;

        public Action<InputFrame> feedInputOrig;

        public RenderTarget2D frameTarget;

        public Color[] fb = new Color[320 * 180];

        public byte[] fbBytes = new byte[320 * 180 * 3];

        public class Message
        {
            public Message(string eventChannel, object data)
            {
                this.eventChannel = eventChannel;
                this.data = data;
            }

            public string eventChannel { private set; get; }
            public object data { private set; get; }
        }

        public BlockingCollection<Message> sendQueue = new BlockingCollection<Message>();

        MethodInfo renderCore;
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
            On.Celeste.Celeste.Update += gameUpdate;
            feedInputDetour = new Detour(typeof(TAS.InputHelper).GetMethod("FeedInputs", BindingFlags.Static | BindingFlags.Public), typeof(TAS.patch_InputHelper).GetMethod("FeedInputs", BindingFlags.Static | BindingFlags.Public));
            feedInputOrig = feedInputDetour.GenerateTrampoline<Action<InputFrame>>();
            renderCore = typeof(Celeste).GetMethod("RenderCore", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        public override void Unload()
        {
            // TODO: unapply any hooks applied in Load()
            On.Celeste.Celeste.Initialize -= modInitalize;
            On.Celeste.Celeste.Update -= gameUpdate;
            feedInputDetour.Dispose();
        }

        public void RunSendThread()
        {
            while(true)
            {
                lock (socket)
                {
                    if(socket == null)
                    {
                        break;
                    }
                }

                Message msgToSend = sendQueue.Take();

                lock (socket)
                {
                    socket.Emit(msgToSend.eventChannel, msgToSend.data).GetAwaiter().GetResult();
                }
            }
        }

        public static void RunSendThreadStatic()
        {
            Instance.RunSendThread();
        }

        public void Send(Message msg)
        {
            this.sendQueue.Add(msg);
        }

        public void Send(string eventChannel, object data)
        {
            this.Send(new Message(eventChannel, data));
        }

        bool subscribedOnFrame = false;

        bool subscribedFrameDump = false;

        public void RegisterListeners()
        {
            socket.On("identify", response =>
            {
                Logger.Log(LogLevel.Info, "DatalineModule", "Sending Identity");
                this.Send("identifier", Process.GetCurrentProcess().Id);
            });

            socket.On<ControlMessage>("setBorderless", msg =>
            {
                Celeste.Instance.Window.IsBorderlessEXT = msg.newState ?? false;
            });

            socket.On<ControlMessage>("setNativeInput", msg =>
            {
                blockNativeInput = msg.newState ?? false;
            });

            socket.On<ControlMessage>("setFrameSub", msg =>
            {
                // Logger.Log(LogLevel.Info, "DatalineModule", "sub frame state " + msg.newState);
                subscribedOnFrame = msg.newState ?? false;
            });

            socket.On<ControlMessage>("setFrameDumpSub", msg =>
            {
                Logger.Log(LogLevel.Info, "DatalineModule", "set frame dump sub " + msg.newState);
                subscribedFrameDump = msg.newState ?? false;
            });

            socket.On<ControlMessage>("consoleCommand", msg =>
            {
                Logger.Log(LogLevel.Info, "DatalineModule", "Running console cmd " + msg.command);
                string cmd = (string?) msg.command;
                if(cmd == null)
                {
                    return;
                }
                object[] args = new object[2];
                args[0] = cmd.Split(" ");
                args[1] = cmd;
                typeof(ConsoleCommand).GetMethod("Console", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,args);
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
            Thread t = new Thread(new ThreadStart(RunSendThreadStatic));
            try
            {
                t.Start();
            }
            catch (Exception e)
            {
                Logger.LogDetailed(e);
            }
        }


        private void modInitalize(On.Celeste.Celeste.orig_Initialize orig, Celeste self)
        {
            Log("Dataline Dev revision tas data + more");
            Connect();
            orig(self);
        }

        public void BeforeUpdate(GameTime time)
        {
            
        }

        private void gameUpdate(On.Celeste.Celeste.orig_Update orig, Celeste self, GameTime time)
        {

            this.BeforeUpdate(time);
            orig(self, time);
            this.AfterUpdate(time);

        }

        public void AfterUpdate(GameTime time)
        {
            if (subscribedOnFrame)
            {
                this.Send("frame", null);
                if (subscribedFrameDump)
                {
                    if(frameTarget == null)
                    {
                        frameTarget = new(Celeste.Graphics.GraphicsDevice, 320, 180);
                    }
                    
                    // thanks https://github.com/catapillie/ModderToolkit/blob/master/Code/Tools/Screenshot.cs

                    Engine.Instance.GraphicsDevice.SetRenderTarget(frameTarget);
                    Engine.Instance.GraphicsDevice.Clear(Color.Black);
                    // render
                    renderCore.Invoke(Celeste.Instance, new object[] {});
                    // Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.Default, RasterizerState.CullNone, ColorGrade.Effect);
                    // Draw.SpriteBatch.Draw(GameplayBuffers.Level, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 1f, SaveData.Instance.Assists.MirrorMode ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                    // Draw.SpriteBatch.End();

                    // fix state 
                    Engine.Instance.GraphicsDevice.SetRenderTarget(null);

                    frameTarget.GetData<Color>(fb);
                    for(int i = 0; i < fb.Length; i++)
                    {
                        fbBytes[i * 3 + 0] = fb[i].R;
                        fbBytes[i * 3 + 1] = fb[i].G;
                        fbBytes[i * 3 + 2] = fb[i].B;
                    }

                    this.Send("frameDump", fbBytes);

                }
            }
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
        // public static extern void orig_FeedInputs(InputFrame input);
        public static void FeedInputs(InputFrame input)
        {
            DatalineModule.Instance.Log("input fed " + input);
            // orig_FeedInputs(input);
            DatalineModule.Instance.feedInputOrig(input);
        }
    }
}