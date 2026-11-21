using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using ARCA.Central;

namespace ARCA.Identificar
{
    /// <summary>
    /// Fala com o ARCA Central, no notebook, pelo cabo. É a única saída de rede
    /// deste programa: o Vista não tem rota para a internet, e aqui não há proxy.
    /// </summary>
    public static class ClienteCentral
    {
        public static string Endereco
        {
            get
            {
                string s = null;
                try { s = ConfigurationManager.AppSettings["ArcaCentral"]; } catch { }
                return string.IsNullOrEmpty(s) ? "http://192.168.50.1:8130" : s.TrimEnd('/');
            }
        }

        public static TResposta Buscar(string strArtista, string strAlbum, int intFaixas)
        {
            return Enviar("/buscar", new TPedido { strArtista = strArtista, strAlbum = strAlbum, intFaixas = intFaixas });
        }

        public static TResposta PorLink(string strUrl, int intFaixas)
        {
            return Enviar("/link", new TPedido { strUrl = strUrl, intFaixas = intFaixas });
        }

        /// <summary>
        /// A G7000, pelo notebook. Devolve o PNG (sem perda) e a resolução.
        /// Demora: 13 × 13 cm a 300 dpi leva uns 35 s; a mesa inteira, mais de um minuto.
        /// </summary>
        public static byte[] Digitalizar(double dblLarguraCm, double dblAlturaCm, int intDpi, out double dblDpi)
        {
            dblDpi = intDpi;
            JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            byte[] arrPedido = Encoding.UTF8.GetBytes(jss.Serialize(new TPedido { dblLarguraCm = dblLarguraCm, dblAlturaCm = dblAlturaCm, intDpi = intDpi }));
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Endereco + "/digitalizar");
            req.Method = "POST";
            req.Proxy = null;
            req.ContentType = "application/json; charset=utf-8";
            req.ContentLength = arrPedido.Length;
            req.Timeout = 5 * 60 * 1000;
            req.ReadWriteTimeout = 5 * 60 * 1000;
            try
            {
                using (System.IO.Stream s = req.GetRequestStream()) s.Write(arrPedido, 0, arrPedido.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (System.IO.Stream s = resp.GetResponseStream())
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    double d;
                    if (double.TryParse(resp.Headers["X-Dpi"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d > 0) dblDpi = d;
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    try
                    {
                        using (System.IO.StreamReader r = new System.IO.StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            TResposta objR = jss.Deserialize<TResposta>(r.ReadToEnd());
                            if (objR != null && !string.IsNullOrEmpty(objR.strErro)) throw new Exception(objR.strErro);
                        }
                    }
                    catch (ArgumentException) { }
                }
                if (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.Timeout)
                    throw new Exception("o ARCA Central não respondeu em " + Endereco +
                                        ". O notebook está ligado, com o cabo, e o ARCA Central aberto?");
                throw;
            }
        }

        /// <summary>A discografia do artista; enquanto o Central ainda lê, blnPronta vem false com o andamento.</summary>
        public static TDiscografiaDto Discografia(string strArtista)
        {
            return Enviar<TDiscografiaDto>("/discografia", new TPedido { strArtista = strArtista });
        }

        /// <summary>O arquivo da base do AccurateRip para o disco do índice (só o download; a conta é no Vista).</summary>
        public static TResposta AccurateRip(string strMcdi, int intFaixasAudio)
        {
            return Enviar<TResposta>("/accuraterip", new TPedido { strMcdi = strMcdi, intFaixasAudio = intFaixasAudio }, 60000);
        }

        /// <summary>O disco pelo som das faixas (AcoustID): o Central lê cada uma pelo compartilhamento. Demora uns 2 s por faixa.</summary>
        public static TResposta PorSom(List<string> lstCaminhos)
        {
            return Enviar<TResposta>("/som", new TPedido { lstCaminhos = lstCaminhos }, 10 * 60 * 1000);
        }

        /// <summary>O disco pelo índice do CD que o WMP grava nas faixas (WM/MCDI): MusicBrainz e GnuDB, em lstCandidatos.</summary>
        public static TResposta PorToc(string strMcdi)
        {
            return Enviar("/toc", new TPedido { strMcdi = strMcdi });
        }

        /// <summary>As músicas do Apple Music para um título (e artista, se houver). Do cache do Central, ou ~3 s pela rede.</summary>
        public static TBuscaFaixaDto Faixa(string strArtista, string strTitulo)
        {
            return Enviar<TBuscaFaixaDto>("/faixa", new TPedido { strArtista = strArtista, strTitulo = strTitulo });
        }

        private static TResposta Enviar(string strCaminho, TPedido objPedido)
        {
            return Enviar<TResposta>(strCaminho, objPedido);
        }

        /// <summary>WebClient com prazo próprio (o padrão é 100 s; a busca pelo som de um álbum inteiro passa disso).</summary>
        private sealed class TClienteComPrazo : WebClient
        {
            public int intPrazo = 100000;
            protected override WebRequest GetWebRequest(Uri endereco)
            {
                WebRequest w = base.GetWebRequest(endereco);
                w.Timeout = intPrazo;
                HttpWebRequest h = w as HttpWebRequest;
                if (h != null) h.ReadWriteTimeout = intPrazo;
                return w;
            }
        }

        private static T Enviar<T>(string strCaminho, TPedido objPedido)
        {
            return Enviar<T>(strCaminho, objPedido, 100000);
        }

        private static T Enviar<T>(string strCaminho, TPedido objPedido, int intPrazo)
        {
            JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            using (TClienteComPrazo wc = new TClienteComPrazo { intPrazo = intPrazo })
            {
                wc.Proxy = null;
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                string strResposta;
                try { strResposta = wc.UploadString(Endereco + strCaminho, "POST", jss.Serialize(objPedido)); }
                catch (WebException ex)
                {
                    if (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.Timeout)
                        throw new Exception("o ARCA Central não respondeu em " + Endereco +
                                            ". O notebook está ligado, com o cabo, e o ARCA Central aberto?");
                    throw;
                }
                return jss.Deserialize<T>(strResposta);
            }
        }
    }
}
