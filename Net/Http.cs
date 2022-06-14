using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Net
{
    public class Http
    {
        public delegate void HttpGetAsyncProgressDelegate(string url, long totalSize, long size);

        public delegate void HttpGetAsyncCompletedDelegate(string url, byte[] bytes);

        /// </summary>
        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.102 Safari/537.36 Edg/98.0.1108.62";

        /// <summary>
        /// 内容类型
        /// </summary>
        private const string DefaultContentType =
            "application/x-www-form-urlencoded"; //application/x-www-form-urlencoded  //text/html;charset=UTF-8   //multipart/form-data

        /// <summary>
        /// 字符编码
        /// </summary>
        private static readonly Encoding DefaultEncoding = Encoding.UTF8;

        private static readonly Queue<byte[]> BufferPool = new Queue<byte[]>();
        private static readonly Queue<MemoryStream> MemoryStreamPool = new Queue<MemoryStream>();

        public static string Get(
            string url,
            Dictionary<string, string> args = null,
            Encoding encoding = null,
            string contentType = DefaultContentType, string userAgent = DefaultUserAgent)
        {
            var argsStr = args == null ? string.Empty : FormatKeyValuePairs(args);
            url = argsStr == string.Empty ? url : url + "?" + argsStr;
            encoding = encoding ?? DefaultEncoding;
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            try
            {
                ServiceSetting();
                contentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
                userAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
                request = CreateHttpWebRequest(url, "GET", contentType, userAgent);
                response = (HttpWebResponse) request.GetResponse();
                using (var streamReader = new StreamReader(response.GetResponseStream(), encoding))
                    result = streamReader.ReadToEnd();
                // Debug.Log($"Http [GET] url:{url}");
            }
            catch (Exception e)
            {
                // Debug.LogError($"Http [GET] Error url:{url},\nerrorInfo:{e}");
            }
            finally
            {
                if (request != null)
                    request.Abort();
                if (response != null)
                    response.Close();
            }

            return result;
        }

        public static async Task<string> GetAsync(string url, Dictionary<string, string> args = null,
            Encoding encoding = null, string contentType = DefaultContentType, string userAgent = DefaultUserAgent)
        {
            var argsStr = args == null ? string.Empty : FormatKeyValuePairs(args);
            url = argsStr == string.Empty ? url : url + "?" + argsStr;
            encoding = encoding ?? DefaultEncoding;
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            try
            {
                ServiceSetting();
                contentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
                userAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
                request = CreateHttpWebRequest(url, "GET", contentType, userAgent);
                response = (HttpWebResponse) await request.GetResponseAsync();
                using (var streamReader = new StreamReader(response.GetResponseStream(), encoding))
                    result = await streamReader.ReadToEndAsync();
                // Debug.Log($"Http [GET] url:{url}");
            }
            catch (Exception e)
            {
                // Debug.LogError($"Http [GET] Error url:{url},\nerrorInfo:{e}");
            }
            finally
            {
                if (request != null)
                    request.Abort();
                if (response != null)
                    response.Close();
            }

            return result;
        }

        public static string Post(string url, Dictionary<string, string> args = null,
            Encoding encoding = null, string contentType = DefaultContentType, string userAgent = DefaultUserAgent)
        {
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            encoding = encoding ?? DefaultEncoding;
            try
            {
                ServiceSetting();
                contentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
                userAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
                request = CreateHttpWebRequest(url, "POST", contentType, userAgent);
                //默认httpWebRequest使用的是代理请求，获取结果相对较慢。这里直接设置为null，表示不使用代理。速度会提升！
                request.Proxy = null;

                if (args != null)
                {
                    var argsStr = FormatKeyValuePairs(args);
                    var bytes = encoding.GetBytes(argsStr);
                    request.ContentLength = bytes.Length;
                    using (var streamWriter = request.GetRequestStream())
                        streamWriter.Write(bytes, 0, bytes.Length);
                }

                response = (HttpWebResponse) request.GetResponse();
                using (var streamReader = new StreamReader(response.GetResponseStream(), encoding))
                    result = streamReader.ReadToEnd();
            }
            catch (Exception e)
            {
                // Debug.LogError($"Http [POST] Error url:{url},\nerrorInfo:{e}");
            }
            finally
            {
                if (request != null)
                    request.Abort();

                if (response != null)
                    response.Close();
            }

            return result;
        }

        public static async Task<string> PostAsync(string url, Dictionary<string, string> args = null,
            Encoding encoding = null, string contentType = DefaultContentType, string userAgent = DefaultUserAgent)
        {
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            encoding = encoding ?? DefaultEncoding;
            try
            {
                ServiceSetting();
                contentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
                userAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
                request = CreateHttpWebRequest(url, "POST", contentType, userAgent);
                //默认httpWebRequest使用的是代理请求，获取结果相对较慢。这里直接设置为null，表示不使用代理。速度会提升！
                request.Proxy = null;
                if (args != null)
                {
                    var argsStr = FormatKeyValuePairs(args);
                    var bytes = encoding.GetBytes(argsStr);
                    request.ContentLength = bytes.Length;
                    using (var streamWriter = await request.GetRequestStreamAsync())
                        await streamWriter.WriteAsync(bytes, 0, bytes.Length);
                }

                response = (HttpWebResponse) await request.GetResponseAsync();
                using (var streamReader = new StreamReader(response.GetResponseStream(), encoding))
                    result = await streamReader.ReadToEndAsync();
            }
            catch (Exception e)
            {
                // Debug.LogError($"Http [POST] Error url:{url},\nerrorInfo:{e}");
            }
            finally
            {
                if (request != null)
                    request.Abort();

                if (response != null)
                    response.Close();
            }

            return result;
        }

        public static async Task<byte[]> Download(string url, HttpGetAsyncProgressDelegate progressCallback = null,
            string contentType = DefaultContentType, string userAgent = DefaultUserAgent)
        {
            byte[] result = null;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            var buffer = BufferPool.Count > 0 ? BufferPool.Dequeue() : new byte[10240];
            var memoryStream = MemoryStreamPool.Count > 0 ? MemoryStreamPool.Dequeue() : new MemoryStream();
            try
            {
                contentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
                userAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
                request = CreateHttpWebRequest(url, "GET", contentType, userAgent);
                response = (HttpWebResponse) await request.GetResponseAsync();
                var contentLength = response.ContentLength;
                var stream = response.GetResponseStream();
                int size = 0, downloadedBytes = 0;
                memoryStream.SetLength(0);
                while (stream != null && (size =
                    await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    downloadedBytes += size;
                    await memoryStream.WriteAsync(buffer, 0, size);

                    if (progressCallback != null)
                        progressCallback(url, contentLength, downloadedBytes);
                }

                result = memoryStream.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError($"Download Error url:{url},\nerrorInfo:{e}");
            }
            finally
            {
                if (request != null)
                    request.Abort();
                if (response != null)
                    response.Close();
                BufferPool.Enqueue(buffer);
                MemoryStreamPool.Enqueue(memoryStream);
            }

            return result;
        }

        public static IEnumerator Download(string url, HttpGetAsyncProgressDelegate progressCallback = null,
            HttpGetAsyncCompletedDelegate completedCallback = null, string contentType = DefaultContentType,
            string userAgent = DefaultUserAgent)
        {
            var task = Download(url, progressCallback, contentType, userAgent);

            while (!task.IsCompleted)
            {
                if (task.IsCanceled || task.IsFaulted)
                    yield break;

                yield return null;
            }

            var result = task.Result;

            if (completedCallback != null)
                completedCallback(url, result);
        }

        public static void ClearPool()
        {
            BufferPool.Clear();
            while (MemoryStreamPool.Count > 0)
            {
                var memoryStream = MemoryStreamPool.Dequeue();
                memoryStream.Dispose();
            }
        }

        private static void ServiceSetting()
        {
            //ServicePointManager.Expect100Continue = false; //绕过代理服务器
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 |
                                                   SecurityProtocolType.Tls12 | SecurityProtocolType.Ssl3;
            ServicePointManager.ServerCertificateValidationCallback = CheckValidationResult;
        }

        private static bool CheckValidationResult(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors errors) => true;

        private static string FormatKeyValuePairs(Dictionary<string, string> @params)
        {
            var stringBuilder = new StringBuilder();
            var index = 0;

            const string format = "{0}={1}";
            foreach (var key in @params.Keys)
                stringBuilder.AppendFormat(index++ > 0 ? "&" + format : format, key, @params[key]);
            return stringBuilder.ToString();
        }

        private static HttpWebRequest CreateHttpWebRequest(string url, string method, string contentType,
            string userAgent)
        {
            var request = (HttpWebRequest) WebRequest.Create(url);
            request.Method = method;
            request.ContentType = string.IsNullOrEmpty(contentType) ? DefaultContentType : contentType;
            request.UserAgent = string.IsNullOrEmpty(userAgent) ? DefaultUserAgent : userAgent;
            request.Accept = "*/*";
            request.ProtocolVersion = HttpVersion.Version11;

            return request;
        }
    }
}