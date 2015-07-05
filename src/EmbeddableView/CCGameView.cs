﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace CocosSharp
{
    using XnaSurfaceFormat = Microsoft.Xna.Framework.Graphics.SurfaceFormat;

    public enum CCViewResolutionPolicy
    {
        Custom,         // Use ViewportRectRatio
        ExactFit,       // Fit to entire view. Distortion may occur
        NoBorder,       // Maintain design resolution aspect ratio, but scene may appear cropped
        ShowAll,        // Maintain design resolution aspect ratio, ensuring entire scene is visible
        FixedHeight,    // Use width of design resolution and scale height to aspect ratio of view
        FixedWidth      // Use height of design resolution and scale width to aspect ratio of view 

    }

    public partial class CCGameView
    {
        // (10 mill ticks per second / 60 fps) (rounded up)
        const int numOfTicksPerUpdate = 166667; 
        const int maxUpdateTimeMilliseconds = 500;

        static readonly CCRect exactFitViewportRatio = new CCRect(0,0,1,1);

        class CCGraphicsDeviceService : IGraphicsDeviceService
        {
            public GraphicsDevice GraphicsDevice { get; private set; }

            public CCGraphicsDeviceService(GraphicsDevice graphicsDevice)
            {
                GraphicsDevice = graphicsDevice;
            }

            public event EventHandler<EventArgs> DeviceCreated;
            public event EventHandler<EventArgs> DeviceDisposing;
            public event EventHandler<EventArgs> DeviceReset;
            public event EventHandler<EventArgs> DeviceResetting;
        }

        public event EventHandler<EventArgs> ViewCreated;

        internal delegate void ViewportChangedEventHandler(CCGameView sender);
        internal event ViewportChangedEventHandler ViewportChanged;

        bool touchEnabled;
        bool gameStarted;
        bool viewportDirty;

        CCViewResolutionPolicy resolutionPolicy;
        CCRect viewportRatio;
        CCSize designResolution;
        Viewport viewport;

        GraphicsDevice graphicsDevice;
        CCGraphicsDeviceService graphicsDeviceService;
        GameServiceContainer servicesContainer;

        GameTime gameTime;
        TimeSpan accumulatedElapsedTime;
        TimeSpan targetElapsedTime;
        TimeSpan maxElapsedTime;
        Stopwatch gameTimer;
        long previousTicks;

        // Touch handling
        Dictionary<int, CCTouch> touchMap;
        List<CCTouch> incomingNewTouches;
        List<CCTouch> incomingMoveTouches;
        List<CCTouch> incomingReleaseTouches;


        #region Properties

        public static CCViewResolutionPolicy DefaultResolutionPolicy { get; set; }
        public static CCSize DefaultDesignResolution { get; set; }


        #if !NETFX_CORE
        public CCAccelerometer Accelerometer { get; set; }
        #endif

        public CCDirector Director { get; private set; }
        public CCRenderer Renderer { get { return DrawManager != null ? DrawManager.Renderer : null; } }
        public CCActionManager ActionManager { get; private set; }

        public bool DepthTesting
        {
            get { return Renderer.UsingDepthTest; }
            set { Renderer.UsingDepthTest = value; }
        }

        public bool DisplayStats 
        {
            get { return Stats.IsEnabled; }
            set { Stats.IsEnabled = value; }
        }

        public bool TouchEnabled
        {
            get { return touchEnabled; }
            set
            {
                touchEnabled = value;
                PlatformUpdateTouchEnabled();
            }
        }

        public int StatsScale
        {
            get { return Stats.Scale; }
            set { Stats.Scale = value; }
        }

        public CCViewResolutionPolicy ResolutionPolicy 
        { 
            get { return resolutionPolicy; }
            set 
            {
                resolutionPolicy = value;

                // Reset ratio if using custom resolution policy
                if(resolutionPolicy == CCViewResolutionPolicy.Custom)
                    viewportRatio = exactFitViewportRatio;
                viewportDirty = true;
            }
        }

        public CCSize DesignResolution
        {
            get { return designResolution; }
            set
            {
                designResolution = value;
                viewportDirty = true;
            }
        }

        public CCSizeI ViewSize
        {
            get; private set;
        }

        public CCRect ViewportRectRatio
        {
            get { return viewportRatio; }
            set 
            {
                viewportRatio = value;
                resolutionPolicy = CCViewResolutionPolicy.Custom;
                viewportDirty = true;
            }
        }

        internal CCEventDispatcher EventDispatcher { get; private set; }
        internal CCDrawManager DrawManager { get; private set; }

        internal Viewport Viewport 
        {
            get 
            { 
                if(viewportDirty) 
                    UpdateViewport(); 
                return viewport; 
            }
            private set 
            {
                viewport = value;
                ViewportChanged(this);
            }
        }

        CCStats Stats { get; set; }

        #endregion Properties


        #region Initialisation

        static CCGameView()
        {
            DefaultResolutionPolicy = CCViewResolutionPolicy.ShowAll;
        }

        public void StartGame()
        {
            if(!gameStarted)
            {
                PlatformStartGame();
                gameStarted = true;
            }
        }

        public void RunWithScene(CCScene scene)
        {
            StartGame();
            Director.RunWithScene(scene);
        }
            
        void Initialise()
        {
            PlatformInitialise();

            ActionManager = new CCActionManager();
            Director = new CCDirector();
            EventDispatcher = new CCEventDispatcher(this);

            DesignResolution = DefaultDesignResolution;
            ViewportRectRatio = exactFitViewportRatio;
            ResolutionPolicy = CCViewResolutionPolicy.ShowAll;

            //Stats.Initialize();

            InitialiseGraphicsDevice();

            InitialiseRunLoop();

            InitialiseTouchHandling();

            ViewCreated(this, null);
        }

        void InitialiseGraphicsDevice()
        {
            var graphicsProfile = GraphicsProfile.HiDef;

            var presParams = new PresentationParameters();
            presParams.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            presParams.DepthStencilFormat = DepthFormat.Depth24Stencil8;
            presParams.BackBufferFormat = XnaSurfaceFormat.Color;
            presParams.RenderTargetUsage = RenderTargetUsage.PreserveContents;

            graphicsDevice = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, graphicsProfile, presParams);
            DrawManager = new CCDrawManager(graphicsDevice);

            // Fix this!
            CCDrawManager.SharedDrawManager = DrawManager;

            graphicsDeviceService = new CCGraphicsDeviceService(graphicsDevice);

            var serviceProvider = CCContentManager.SharedContentManager.ServiceProvider as GameServiceContainer;
            serviceProvider.AddService(typeof(IGraphicsDeviceService), graphicsDeviceService);
        }

        void InitialiseRunLoop()
        {
            gameTimer = Stopwatch.StartNew();
            gameTime = new GameTime();

            accumulatedElapsedTime = TimeSpan.Zero;
            targetElapsedTime = TimeSpan.FromTicks(numOfTicksPerUpdate); 
            maxElapsedTime = TimeSpan.FromMilliseconds(maxUpdateTimeMilliseconds);
            previousTicks = 0;
        }

        void InitialiseTouchHandling()
        {
            touchMap = new Dictionary<int, CCTouch>();
            incomingNewTouches = new List<CCTouch>();
            incomingMoveTouches = new List<CCTouch>();
            incomingReleaseTouches = new List<CCTouch>();

            TouchEnabled = true;
        }

        #endregion Initialisation


        #region Drawing

        internal void Present()
        {
            PlatformPresent();
        }

        void UpdateViewport()
        {
            int width = ViewSize.Width;
            int height = ViewSize.Height;

            // The GraphicsDevice BackBuffer dimensions are used by MonoGame when laying out the viewport
            // so make sure they're updated
            graphicsDevice.PresentationParameters.BackBufferWidth = width;
            graphicsDevice.PresentationParameters.BackBufferHeight = height;

            if (resolutionPolicy != CCViewResolutionPolicy.Custom)
            {
                float resolutionScaleX = width / DesignResolution.Width;
                float resolutionScaleY = height / DesignResolution.Height;

                switch (resolutionPolicy)
                {
                    case CCViewResolutionPolicy.NoBorder:
                        resolutionScaleX = resolutionScaleY = Math.Max(resolutionScaleX, resolutionScaleY);
                        break;
                    case CCViewResolutionPolicy.ShowAll:
                        resolutionScaleX = resolutionScaleY = Math.Min(resolutionScaleX, resolutionScaleY);
                        break;
                    case CCViewResolutionPolicy.FixedHeight:
                        resolutionScaleX = resolutionScaleY;
                        designResolution.Width = (float)Math.Ceiling(width / resolutionScaleX);
                        break;
                    case CCViewResolutionPolicy.FixedWidth:
                        resolutionScaleY = resolutionScaleX;
                        designResolution.Height = (float)Math.Ceiling(height / resolutionScaleY);
                        break;
                    default:
                        break;
                }

                float viewPortW = DesignResolution.Width * resolutionScaleX;
                float viewPortH = DesignResolution.Height * resolutionScaleY;

                CCRect viewPortRect = new CCRect((width - viewPortW) / 2, (height - viewPortH) / 2, 
                    viewPortW, viewPortH);

                viewportRatio = new CCRect(
                    ((viewPortRect.Origin.X) / width),
                    ((viewPortRect.Origin.Y) / height),
                    ((viewPortRect.Size.Width) / width),
                    ((viewPortRect.Size.Height) / height)
                );
            }

            viewportDirty = false;

            Viewport = new Viewport((int)(width * viewportRatio.Origin.X), (int)(height * viewportRatio.Origin.Y), 
                (int)(width * viewportRatio.Size.Width), (int)(height * viewportRatio.Size.Height));
        }

        void Draw()
        {
            DrawManager.BeginDraw();

            CCScene runningScene = Director.RunningScene;

            var vp = Viewport;

            if (runningScene != null) 
            {
                Renderer.PushViewportGroup(ref vp);

                runningScene.Visit();

                Renderer.PopViewportGroup();

                Renderer.VisitRenderQueue();
            }

            DrawManager.EndDraw();
        }

        #endregion Drawing


        #region Run loop

        void Tick()
        {
            RetryTick:

            var currentTicks = gameTimer.Elapsed.Ticks;
            accumulatedElapsedTime += TimeSpan.FromTicks(currentTicks - previousTicks);
            previousTicks = currentTicks;

            if (accumulatedElapsedTime < targetElapsedTime)
            {
                var sleepTime = (int)(targetElapsedTime - accumulatedElapsedTime).TotalMilliseconds;

                System.Threading.Thread.Sleep(sleepTime);
                goto RetryTick;
            }

            if (accumulatedElapsedTime > maxElapsedTime)
                accumulatedElapsedTime = maxElapsedTime;

            gameTime.ElapsedGameTime = targetElapsedTime;
            var stepCount = 0;

            while (accumulatedElapsedTime >= targetElapsedTime)
            {
                gameTime.TotalGameTime += targetElapsedTime;
                accumulatedElapsedTime -= targetElapsedTime;
                ++stepCount;

                Update(gameTime);
            }

            gameTime.ElapsedGameTime = TimeSpan.FromTicks(targetElapsedTime.Ticks * stepCount);
        }

        void Update(GameTime time)
        {
            float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (Director.NextScene != null)
                Director.SetNextScene();

            CCScheduler.SharedScheduler.Update(deltaTime);
            ActionManager.Update(deltaTime);

            ProcessTouches();
        }

        #endregion Run loop


        #region Touch handling

        void AddIncomingNewTouch(int touchId, ref CCPoint position)
        {
            if (!touchMap.ContainsKey(touchId))
            {
                var touch = new CCTouch(touchId, position);
                touchMap.Add(touchId, touch);
                incomingNewTouches.Add(touch);
            }
        }

        void UpdateIncomingMoveTouch(int touchId, ref CCPoint position)
        {
            CCTouch existingTouch;
            if (touchMap.TryGetValue(touchId, out existingTouch))
            {
                var delta = existingTouch.LocationOnScreen - position;
                if (delta.LengthSquared > 1.0f)
                {
                    incomingMoveTouches.Add(existingTouch);
                    existingTouch.SetTouchInfo(touchId, position.X, position.Y);
                }
            }
        }

        void UpdateIncomingReleaseTouch(int touchId)
        {
            CCTouch existingTouch;
            if (touchMap.TryGetValue(touchId, out existingTouch))
            {
                incomingReleaseTouches.Add(existingTouch);
                touchMap.Remove(touchId);
            }
        }

        void ProcessTouches()
        {
            if (EventDispatcher.IsEventListenersFor(CCEventListenerTouchOneByOne.LISTENER_ID)
                || EventDispatcher.IsEventListenersFor(CCEventListenerTouchAllAtOnce.LISTENER_ID))
            {
                var touchEvent = new CCEventTouch(CCEventCode.BEGAN);

                if (incomingNewTouches.Count > 0)
                {
                    touchEvent.EventCode = CCEventCode.BEGAN;
                    touchEvent.Touches = incomingNewTouches;
                    EventDispatcher.DispatchEvent(touchEvent);
                }

                if (incomingMoveTouches.Count > 0)
                {
                    touchEvent.EventCode = CCEventCode.MOVED;
                    touchEvent.Touches = incomingMoveTouches;
                    EventDispatcher.DispatchEvent(touchEvent);
                }

                if (incomingReleaseTouches.Count > 0)
                {
                    touchEvent.EventCode = CCEventCode.ENDED;
                    touchEvent.Touches = incomingReleaseTouches;
                    EventDispatcher.DispatchEvent(touchEvent);
                }

                incomingNewTouches.Clear();
                incomingMoveTouches.Clear();
                incomingReleaseTouches.Clear();
            }
        }

        #endregion Touch handling

    }
}

