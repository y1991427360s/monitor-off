using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace MonitorOff;

public static class Program
{
    public const string PipeName = "MonitorOff.Pipe.v1";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\MonitorOff.App.v1", out bool first);
        if (!first)
        {
            // 已在运行：把参数交给正在运行的实例处理（无参数 = 显示窗口）
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(2000);
                using var w = new StreamWriter(c);
                w.WriteLine(args.Length == 0 ? "--show" : string.Join(' ', args));
            }
            catch { }
            return;
        }

        Trace("main");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new MainWindow();
        Trace("window-created");
        win.ContentRendered += (_, _) => Trace("rendered");
        win.Start(args);
        Trace("started");
        app.Run();
    }

    public static void Trace(string what)
    {
        if (Environment.GetEnvironmentVariable("MONITOROFF_TRACE") != "1") return;
        var since = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "monitoroff-trace.log"), $"{since.TotalMilliseconds:0} ms  {what}\n"); } catch { }
    }
}
