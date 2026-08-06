using System;
using System.IO;
using System.Net;
using System.Text;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// Talks to the PlotBridge server. Deliberately WebRequest rather than
    /// HttpClient: this runs inside devenv.exe, and a static HttpClient living for
    /// the lifetime of the IDE is a worse trade than a short synchronous request
    /// made while the debuggee is already stopped at a breakpoint.
    /// </summary>
    internal static class PlotBridgeClient
    {
        public static int Port = 8777;

        private static string Base => $"http://localhost:{Port}";

        public static bool IsRunning()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Base + "/health");
                req.Timeout = 1500;
                req.Method = "GET";
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Posts the raw value strings as text/plain and lets the server
        /// parse them, so number extraction lives in one place.</summary>
        public static bool PushText(string board, string chart, string series, string mode, bool replace, string text, out string message)
        {
            var url = $"{Base}/push?board={Uri.EscapeDataString(board)}" +
                      $"&chart={Uri.EscapeDataString(chart)}" +
                      $"&series={Uri.EscapeDataString(series)}" +
                      $"&replace={(replace ? "true" : "false")}";
            if (!string.IsNullOrEmpty(mode)) url += "&mode=" + Uri.EscapeDataString(mode);

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "text/plain; charset=utf-8";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 30000;

                var body = Encoding.UTF8.GetBytes(text);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream() ?? Stream.Null))
                {
                    message = reader.ReadToEnd();
                    return true;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse r)
                {
                    using (var reader = new StreamReader(r.GetResponseStream() ?? Stream.Null))
                        message = $"server refused the push ({(int)r.StatusCode}): {reader.ReadToEnd()}";
                }
                else
                {
                    message = $"no PlotBridge server on port {Port} — start it with: dotnet run --project PlotBridge/server";
                }
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static string PageUrl(string board) => $"{Base}/?board={Uri.EscapeDataString(board)}";
    }
}
