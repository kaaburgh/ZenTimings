using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace ZenTimings.Cli
{
    // Sequential scheduling only. Acquisition uses the existing one-shot code.
    internal static class WatchLoop
    {
        internal const int DefaultInterval=2, MaxInterval=3600, MaxSamples=86400;
        internal sealed record Frame(long sample_index, DateTimeOffset capture_started_utc);

        internal static int ParsePositive(string value,int max,string option)
        {
            if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int n) || n<1 || n>max)
                throw new ArgumentException($"{option} requires an integer from 1 to {max}.");
            return n;
        }

        // Wait only after capture completion, never overlap hardware requests.
        // A nonzero primary result terminates the stream; no busy retry.
        internal static int Run(int interval,int? samples,Func<Frame,int> capture,CancellationToken token,
            Func<TimeSpan,CancellationToken,bool> wait=null)
        {
            if(interval<1 || interval>MaxInterval || (samples.HasValue && (samples.Value<1 || samples.Value>MaxSamples)))
                throw new ArgumentOutOfRangeException(nameof(interval));
            wait ??= (duration,ct)=>ct.WaitHandle.WaitOne(duration);
            for(long i=1; !token.IsCancellationRequested; i++)
            {
                int status=capture(new Frame(i,DateTimeOffset.UtcNow));
                if(status!=0) return status;
                if(token.IsCancellationRequested || (samples.HasValue && i>=samples.Value)) return 0;
                if(wait(TimeSpan.FromSeconds(interval),token)) return 0;
            }
            return 0;
        }

        // One complete JSON object per line. Keep the existing one-shot schema
        // intact in the nested result, not concatenated pretty-printed JSON.
        internal static string Serialize(Frame frame,object result) => JsonSerializer.Serialize(new {
            watch_schema_version=1,frame.sample_index,frame.capture_started_utc,
            capture_finished_utc=DateTimeOffset.UtcNow,result
        });
    }
}
