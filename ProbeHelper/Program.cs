using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.ProbeHelper
{
    /// <summary>
    /// Out-of-process serial probe. Opening a not-yet-ready MOZA CDC-ACM port
    /// under Wine can SEGFAULT inside Wine's serial-comm layer (the
    /// freshly-powered-base crash) — an uncatchable native fault. Running the
    /// open here means that fault kills only this short-lived throwaway process;
    /// SimHub reads the result over stdout/exit-code and stays alive.
    ///
    /// <para>Usage: <c>MozaProbeHelper.exe &lt;base|hub|ab9&gt; &lt;COMnn&gt;</c>.
    /// Prints exactly one token to stdout: <c>RESP</c> (a MOZA of that kind
    /// answered), <c>REACH</c> (port opened but no matching response), or
    /// <c>NONE</c> (port could not be opened / bad args). A crash or non-zero
    /// exit with no token ⇒ the caller treats the port as not-reachable.</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2 || !SerialProbeCore.TryParseKind(args[0], out var kind))
            {
                Console.Out.WriteLine("NONE");
                return 2;
            }

            string port = args[1];
            try
            {
                var (responded, reachable) = SerialProbeCore.ProbeOnePort(
                    port, kind, m => { try { Console.Error.WriteLine(m); } catch { } });
                Console.Out.WriteLine(responded ? "RESP" : (reachable ? "REACH" : "NONE"));
                return 0;
            }
            catch
            {
                // Managed exception (a native segfault wouldn't reach here — the
                // process just dies, which the caller handles via exit code).
                Console.Out.WriteLine("NONE");
                return 1;
            }
        }
    }
}
