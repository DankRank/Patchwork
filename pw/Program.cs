using Patchwork;
using Patchwork.Utility;
using Serilog;

namespace pw
{
    class Program
    {
        static int Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
            if (args.Length < 3) {
                Log.Logger.Information("Usage: pw INPUT OUTPUT PATCH [PATCH...]");
                return 1;
            }
            var progress = new ProgressObject();
            progress.TaskTitle.HasChanged += (obj) => Log.Logger.Information("-- " + obj.Value);
            progress.TaskText.HasChanged += (obj) => Log.Logger.Information(obj.Value);
            string target = args[0];
            string output = args[1];
            var patcher = new AssemblyPatcher(target);
            for (int i = 2; i < args.Length; i++)
                patcher.PatchAssembly(args[i], progress);
            patcher.WriteTo(output);
            return 0;
        }
    }
}
