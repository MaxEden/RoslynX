using System;
using System.Diagnostics;

namespace Test
{
    public struct Measurer : IDisposable
    {
        private string                   _msg;
        private Action<string, TimeSpan> _log;
        private Stopwatch                _stopwatch;

        public Measurer(string msg, Action<string, TimeSpan> log = null)
        {
            _msg = msg;
            _log = log;
            if(log == null) _log = Log;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _log(_msg, _stopwatch.Elapsed);
            _msg = null;
            _log = null;
            _stopwatch = null;
        }

        public static void Log(string arg1, TimeSpan arg2)
        {
            Console.WriteLine($"Finished {arg1} in {arg2.TotalMilliseconds} ms");
        }
    }
}