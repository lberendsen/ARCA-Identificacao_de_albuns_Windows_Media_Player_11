using System;
using System.Configuration;
using System.IO;
using System.Net;

namespace ARCA.Modelo
{
    public static class NetworkConfiguration
    {
        private static readonly object s_objTrava = new object();
        private static bool s_blnIniciado;

        /// <summary>
        /// TLS 1.2 e a validação de certificados, uma vez por processo. É
        /// chamado por CreateProxy, que toda requisição usa — então nenhum
        /// serviço depende mais de outro ter sido chamado antes (o Discogs
        /// como primeira consulta falhava no handshake).
        /// </summary>
        public static void Initialize()
        {
            lock (s_objTrava)
            {
                if (s_blnIniciado) return;
                s_blnIniciado = true;

                // Forçar TLS 1.2
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                // A validação dos certificados é a padrão do Windows, sem exceção nenhuma.
                // (Uma exceção pelo nome do emissor seria falsificável: qualquer um põe o nome
                // que quiser num certificado.)
            }
        }

        /// <summary>
        /// Faz a requisição. Em erro HTTP, fecha a resposta que vem dentro da
        /// exceção antes de repassá-la: aberta, ela prende uma das conexões do
        /// proxy até o coletor de lixo passar, e as consultas seguintes ficam
        /// esperando.
        /// </summary>
        public static HttpWebResponse Responder(HttpWebRequest req)
        {
            try { return (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                // O código HTTP fica guardado na própria exceção: depois de
                // fechada, a resposta não pode mais ser consultada.
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) { try { ex.Data[ChaveStatusHttp] = (int)resp.StatusCode; } catch { } }
                if (ex.Response != null) { try { ex.Response.Close(); } catch { } }
                throw;
            }
        }

        public const string ChaveStatusHttp = "ARCA.StatusHttp";

        /// <summary>O código HTTP de uma falha vinda de Responder, ou 0.</summary>
        public static int StatusHttp(WebException ex)
        {
            return ex != null && ex.Data.Contains(ChaveStatusHttp) ? (int)ex.Data[ChaveStatusHttp] : 0;
        }

        /// <summary>
        /// Lê o corpo inteiro com teto: servidor que não informa o tamanho
        /// (resposta em partes) não pode encher a memória.
        /// </summary>
        public static byte[] LerCorpo(HttpWebResponse resp, int intMaximo)
        {
            if (resp.ContentLength > intMaximo) throw new InvalidDataException(string.Format("Resposta maior que o limite ({0:N0} bytes).", intMaximo));
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream m = new MemoryStream())
            {
                byte[] buf = new byte[65536];
                int r;
                while ((r = s.Read(buf, 0, buf.Length)) > 0)
                {
                    m.Write(buf, 0, r);
                    if (m.Length > intMaximo) throw new InvalidDataException(string.Format("Resposta maior que o limite ({0:N0} bytes).", intMaximo));
                }
                return m.ToArray();
            }
        }

        public static IWebProxy CreateProxy()
        {
            Initialize();
            return ProxyConfigurado() ?? WebRequest.DefaultWebProxy;
        }

        /// <summary>O proxy do App.config, ou null se não houver.</summary>
        private static IWebProxy ProxyConfigurado()
        {
            string enabled = null;
            string host = null;
            int port = 3128;
            try
            {
                enabled = ConfigurationManager.AppSettings["ProxyEnabled"];
                host = ConfigurationManager.AppSettings["ProxyHost"];
                int.TryParse(ConfigurationManager.AppSettings["ProxyPort"], out port);
            }
            catch { }

            if (string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(enabled) && !string.IsNullOrEmpty(host)))
            {
                if (string.IsNullOrWhiteSpace(host)) host = "192.168.50.1";
                if (port <= 0) port = 3128;
                return new WebProxy(host, port);
            }

            return null;
        }
    }

    public static class ExternalMetadataClient
    {
        public static string FetchString(string url)
        {
            NetworkConfiguration.Initialize();
            var request = CreateRequest(url);
            using (var response = NetworkConfiguration.Responder(request))
            {
                ValidateResponse(response);
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static byte[] FetchImage(string url)
        {
            NetworkConfiguration.Initialize();
            var request = CreateRequest(url);
            using (var response = NetworkConfiguration.Responder(request))
            {
                ValidateResponse(response);
                if (!response.ContentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !response.ContentType.StartsWith("image/png", StringComparison.OrdinalIgnoreCase))
                {
                    // Alguns servidores retornam application/octet-stream; validação pelo magic number é o principal
                }

                if (response.ContentLength > 10 * 1024 * 1024) // 10 MB limit
                {
                    throw new InvalidDataException("Imagem excede o limite de tamanho seguro.");
                }

                using (var stream = response.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    byte[] data = ms.ToArray();

                    bool bIsJpeg = data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
                    bool bIsPng = data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

                    if (!bIsJpeg && !bIsPng)
                    {
                        throw new InvalidDataException("O arquivo baixado não é uma imagem JPEG ou PNG válida.");
                    }
                    return data;
                }
            }
        }

        private static HttpWebRequest CreateRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = NetworkConfiguration.CreateProxy();
            req.UserAgent = "ARCA-Identificacao/1.0 ( https://github.com/lberendsen/ARCA-Identificacao_de_albuns_Windows_Media_Player_11 )";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 3;
            return req;
        }

        private static void ValidateResponse(HttpWebResponse response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new WebException(string.Format("A resposta HTTP não retornou OK. Status: {0}", response.StatusCode));
            }
        }
    }
}
