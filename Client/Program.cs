using Client.Controls;
using Client.Envir;
using Client.Rendering;
using Client.Scenes;
using Library;
using Sentry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Client
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            ConfigReader.Load(Assembly.GetAssembly(typeof(Config)));

            if (Config.SentryEnabled && !string.IsNullOrEmpty(Config.SentryDSN))
            {
                using (SentrySdk.Init(Config.SentryDSN))
                    Init(args);
            }
            else
            {
                Init(args);
            }

            ConfigReader.Save(typeof(Config).Assembly);
        }

        static void Init(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.SetCompatibleTextRenderingDefault(false);

            foreach (KeyValuePair<LibraryFile, string> pair in Libraries.LibraryList)
            {
                if (!File.Exists(@".\" + pair.Value)) continue;

                CEnvir.LibraryList[pair.Key] = new MirLibrary(@".\" + pair.Value);
            }

            CEnvir.Init(args);

            CEnvir.Target = new TargetForm();
            string requestedPipelineId = RenderingPipelineManager.NormalizePipelineId(Config.RenderingPipeline);
            if (!string.Equals(Config.RenderingPipeline, requestedPipelineId, StringComparison.OrdinalIgnoreCase))
                Config.RenderingPipeline = requestedPipelineId;

            string activePipelineId = RenderingPipelineManager.InitializeWithFallback(requestedPipelineId, new RenderingPipelineContext(CEnvir.Target));
            if (!string.Equals(Config.RenderingPipeline, activePipelineId, StringComparison.OrdinalIgnoreCase))
                Config.RenderingPipeline = activePipelineId;
            DXSoundManager.Create();

            DXControl.ActiveScene = new LoginScene(Config.GameSize);

            RenderingPipelineManager.RunMessageLoop(CEnvir.Target, CEnvir.GameLoop);

            CEnvir.Session?.Save(true);
            CEnvir.Unload();
            RenderingPipelineManager.Shutdown();
            DXSoundManager.Unload();
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            CEnvir.HandleFatalException(e.Exception, "UI thread");
            Environment.Exit(1);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception.");
            CEnvir.HandleFatalException(ex, "application domain");
            Environment.Exit(1);
        }
    }
}
