using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Debug = UnityEngine.Debug;

namespace Net
{
    public class Http
    {
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

        public static string Get(string url, Dictionary<string, string> args = null, Encoding encoding = null,
            string contentType = DefaultContentType)
        {
            var argsStr = args == null ? string.Empty : FormatKeyValuePairs(args);
            url = argsStr == string.Empty ? url : url + "?" + argsStr;
            encoding = encoding ?? DefaultEncoding;
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            try
            {
                //ServicePointManager.Expect100Continue = false; //绕过代理服务器
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 |
                                                       SecurityProtocolType.Tls12 | SecurityProtocolType.Ssl3;
                ServicePointManager.ServerCertificateValidationCallback =
                    new RemoteCertificateValidationCallback(CheckValidationResult);
                request = (HttpWebRequest) WebRequest.Create(url);
                request.Method = "GET";
                request.ContentType = string.IsNullOrEmpty(DefaultContentType) ? DefaultContentType : contentType;
                request.UserAgent = DefaultUserAgent;
                request.Accept = "*/*";
                request.ProtocolVersion = HttpVersion.Version11;
                response = (HttpWebResponse) request.GetResponse();
                using (var streamReader = new StreamReader(response.GetResponseStream(), encoding))
                    result = streamReader.ReadToEnd();
                Debug.Log($"Http [GET] url:{url}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Http [GET] Error url:{url},\nerrorInfo:{e}");
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
            Encoding encoding = null, string contentType = DefaultContentType)
        {
            var result = string.Empty;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            encoding = encoding ?? DefaultEncoding;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 |
                                                       SecurityProtocolType.Tls12 | SecurityProtocolType.Ssl3;
                ServicePointManager.ServerCertificateValidationCallback =
                    new RemoteCertificateValidationCallback(CheckValidationResult);

                request = (HttpWebRequest) WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = string.IsNullOrEmpty(DefaultContentType) ? DefaultContentType : contentType;
                request.UserAgent = DefaultUserAgent;
                request.Accept = "*/*";
                request.ProtocolVersion = HttpVersion.Version11;
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
                Debug.LogError($"Http [POST] Error url:{url},\nerrorInfo:{e}");
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

        public static IEnumerator Download(string url, string dst, Action<long, float> onProgress,
            int bufferSize = 4096)
        {
            var error = string.Empty;
            long contentLength = 0, downloadedBytes = 0;
            HttpWebRequest request = null;
            HttpWebResponse response = null;
            Stream inputStream = null, outputStream = null;
            try
            {
                request = (HttpWebRequest) WebRequest.Create(url);
                response = (HttpWebResponse) request.GetResponse();
                inputStream = response.GetResponseStream();
                outputStream = new FileStream(dst, FileMode.Create);
                contentLength = response.ContentLength;
            }
            catch (Exception e)
            {
                error = e.ToString();
            }

            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(error);
            }
            else if (inputStream != null && outputStream != null)
            {
                int size;
                var buffer = new byte[Math.Max(bufferSize, 1024)];
                while ((size = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    downloadedBytes += size;
                    outputStream.Write(buffer, 0, size);
                    var progress = downloadedBytes / (float) contentLength * 100; //当前位置
                    //反馈回调 返回下载进度
                    if (onProgress != null)
                        onProgress(contentLength, progress);
// #if UNITY_EDITOR
                    // Debug.Log($"Download {url},progress:{progress}");
// #endif
                    yield return null;
                }
            }

            inputStream?.Dispose();
            outputStream?.Dispose();

            request?.Abort();
            response?.Dispose();
        }

        public static bool CheckValidationResult(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors errors) => true;

        public static string FormatKeyValuePairs(Dictionary<string, string> @params)
        {
            var stringBuilder = new StringBuilder();
            var index = 0;
            const string format = "{0}={1}";
            foreach (var key in @params.Keys)
                stringBuilder.AppendFormat(index++ > 0 ? "&" + format : format, key, @params[key]);

            return stringBuilder.ToString();
        }
    }
}