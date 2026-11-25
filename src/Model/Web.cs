using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Dispatch.Model
{
    /// <summary>The app's only HTTP: small GETs to public endpoints, with short timeouts and no retries.</summary>
    internal static class Web
    {
        private const int TimeoutMs = 6000;

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
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    return reader.ReadToEnd();
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
