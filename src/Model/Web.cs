using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Dispatch.Model
{
    /// <summary>The app's only HTTP: small GETs to public endpoints, with short timeouts and no retries.</summary>
    internal static class Web
    {
        private const int TimeoutMs = 6000;

        // The answers are a few hundred bytes; anything far bigger or slower is not the API answering.
        private const int MaxChars = 1 << 20;
        private const int DeadlineMs = 10000;

        /// <summary>The response body, or null on any network failure, so callers degrade quietly.</summary>
        public static Task<string> GetTextAsync(string url, string userAgent, string accept = null) => Task.Run(() =>
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = userAgent;
                if (accept != null)
                {
                    request.Accept = accept;
                }

                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                using (new System.Threading.Timer(_ => request.Abort(), null, DeadlineMs, System.Threading.Timeout.Infinite))
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var text = new System.Text.StringBuilder();
                    var buffer = new char[8192];
                    int n;
                    while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        text.Append(buffer, 0, n);
                        if (text.Length > MaxChars)
                        {
                            return null;
                        }
                    }

                    return text.ToString();
                }
            }
            catch (WebException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        });
    }
}
