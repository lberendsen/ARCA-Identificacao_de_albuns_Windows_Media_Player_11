// A discografia de um artista no Discogs: os "masters" em que ele é o artista
// principal, cada um com o ano da edição original e a lista de faixas.
//
// É o que permite organizar acervo em que o álbum ficou sendo só o ano
// ("Roberto Carlos / 1966"): o título de cada faixa e o ano dizem de que disco
// ela é. O casamento em si é feito no Vista, na janela, onde a pessoa confere.
//
// O Discogs atende 60 pedidos por minuto, e um artista como o Roberto tem
// mais de 300 masters. Por isso a leitura roda em segundo plano, com o
// andamento consultável, e tudo fica em cache no disco do notebook: cada
// master uma vez só, para sempre; a lista do artista vale 30 dias.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using ARCA.Modelo;

namespace ARCA.Central
{
    internal static class Discografia
    {
        private const string API = "https://api.discogs.com";
        private const string USER_AGENT = "ARCA-Identificacao/1.0 ( https://github.com/lberendsen/ARCA-Identificacao_de_albuns_Windows_Media_Player_11 )";
        private static readonly TimeSpan INTERVALO = TimeSpan.FromMilliseconds(1100);
        private static readonly TimeSpan VALIDADE_LISTA = TimeSpan.FromDays(30);

        private static readonly object s_objTrava = new object();
        private static readonly Dictionary<string, TDiscografiaDto> s_dicEmAndamento = new Dictionary<string, TDiscografiaDto>();
        private static DateTime s_dtUltimoPedido = DateTime.MinValue;
        private static readonly object s_objTravaRede = new object();

        private static string PastaCache
        {
            get
            {
                string s = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache-discogs");
                Directory.CreateDirectory(s);
                return s;
            }
        }

        /// <summary>Pronta (do cache) ou o andamento da leitura, que começa aqui se ainda não começou.</summary>
        public static TDiscografiaDto Pedir(string strArtista, Action<string> actLog)
        {
            string strChave = Chave(strArtista);
            if (strChave.Length == 0) return new TDiscografiaDto { strErro = "informe o artista" };
            string strArquivo = Path.Combine(PastaCache, "artista-" + strChave + ".json");
            lock (s_objTrava)
            {
                TDiscografiaDto objAndamento;
                if (s_dicEmAndamento.TryGetValue(strChave, out objAndamento))
                {
                    if (!objAndamento.blnPronta && objAndamento.strErro == null) return Copia(objAndamento, false);
                    s_dicEmAndamento.Remove(strChave);
                    if (objAndamento.strErro != null) return Copia(objAndamento, false);
                }
                if (File.Exists(strArquivo) && DateTime.Now - File.GetLastWriteTime(strArquivo) < VALIDADE_LISTA)
                {
                    try
                    {
                        TDiscografiaDto d = Serializador().Deserialize<TDiscografiaDto>(File.ReadAllText(strArquivo, Encoding.UTF8));
                        d.blnPronta = true;
                        return d;
                    }
                    catch (Exception ex) { actLog("cache ilegível, lendo de novo: " + ex.Message); }
                }
                TDiscografiaDto objNova = new TDiscografiaDto { strArtista = strArtista.Trim(), strFase = "procurando o artista no Discogs" };
                s_dicEmAndamento[strChave] = objNova;
                Thread t = new Thread(delegate() { Ler(objNova, strArquivo, actLog); });
                t.IsBackground = true;
                t.Start();
                return Copia(objNova, false);
            }
        }

        private static TDiscografiaDto Copia(TDiscografiaDto d, bool blnComDiscos)
        {
            lock (d)
                return new TDiscografiaDto
                {
                    strArtista = d.strArtista, intArtistaId = d.intArtistaId, blnPronta = d.blnPronta,
                    intLidos = d.intLidos, intTotal = d.intTotal, strFase = d.strFase, strErro = d.strErro,
                    lstDiscos = blnComDiscos ? d.lstDiscos : new List<TDiscoDto>()
                };
        }

        private static void Ler(TDiscografiaDto d, string strArquivo, Action<string> actLog)
        {
            try
            {
                JavaScriptSerializer jss = Serializador();
                string strToken = DiscogsClient.ObterTokenConfigurado();

                // 1. o artista: o resultado cujo nome bate (o Discogs desambigua com "(2)")
                Dictionary<string, object> dicBusca = jss.Deserialize<Dictionary<string, object>>(
                    Pedido(API + "/database/search?type=artist&per_page=10&q=" + Uri.EscapeDataString(d.strArtista), strToken));
                Dictionary<string, object> dicArtista = null;
                foreach (object o in (System.Collections.ArrayList)dicBusca["results"])
                {
                    Dictionary<string, object> r = (Dictionary<string, object>)o;
                    string strNome = System.Text.RegularExpressions.Regex.Replace(Convert.ToString(r["title"]), @"\s*\(\d+\)$", "");
                    if (Chave(strNome) == Chave(d.strArtista)) { dicArtista = r; break; }
                    if (dicArtista == null) dicArtista = r;
                }
                if (dicArtista == null) throw new InvalidOperationException("o Discogs não conhece o artista '" + d.strArtista + "'");
                lock (d)
                {
                    d.intArtistaId = Convert.ToInt32(dicArtista["id"]);
                    d.strArtista = System.Text.RegularExpressions.Regex.Replace(Convert.ToString(dicArtista["title"]), @"\s*\(\d+\)$", "");
                    d.strFase = "listando os discos de " + d.strArtista;
                }
                actLog(string.Format("discografia: {0} = artista {1} no Discogs", d.strArtista, d.intArtistaId));

                // 2. os masters em que ele é o artista principal
                List<Dictionary<string, object>> lstMasters = new List<Dictionary<string, object>>();
                int intPagina = 1, intPaginas = 1, intAvulsos = 0;
                do
                {
                    Dictionary<string, object> dicPag = jss.Deserialize<Dictionary<string, object>>(
                        Pedido(API + "/artists/" + d.intArtistaId + "/releases?sort=year&sort_order=asc&per_page=100&page=" + intPagina, strToken));
                    intPaginas = Convert.ToInt32(((Dictionary<string, object>)dicPag["pagination"])["pages"]);
                    foreach (object o in (System.Collections.ArrayList)dicPag["releases"])
                    {
                        Dictionary<string, object> r = (Dictionary<string, object>)o;
                        if (Convert.ToString(r["role"]) != "Main") continue;
                        if (Convert.ToString(r["type"]) == "master") lstMasters.Add(r); else intAvulsos++;
                    }
                    lock (d) d.strFase = string.Format("listando os discos: página {0} de {1}", intPagina, intPaginas);
                    intPagina++;
                } while (intPagina <= intPaginas && intPagina <= 40);
                actLog(string.Format("discografia: {0} masters como artista principal ({1} lançamentos avulsos, sem master, ficam de fora)", lstMasters.Count, intAvulsos));
                lock (d) { d.intTotal = lstMasters.Count; d.strFase = "lendo as faixas de cada disco"; }

                // 3. as faixas de cada master (cache permanente: um master não muda)
                foreach (Dictionary<string, object> r in lstMasters)
                {
                    int intId = Convert.ToInt32(r["id"]);
                    TDiscoDto disco = null;
                    try { disco = LerMaster(intId, strToken, jss); }
                    catch (Exception ex) { actLog("discografia: master " + intId + " falhou: " + ex.Message); }
                    lock (d)
                    {
                        if (disco != null && disco.lstTitulos.Count > 0) d.lstDiscos.Add(disco);
                        d.intLidos++;
                    }
                }

                lock (d)
                {
                    d.lstDiscos = d.lstDiscos.OrderBy(x => x.intAno).ThenBy(x => x.strTitulo).ToList();
                    File.WriteAllText(strArquivo, jss.Serialize(d), new UTF8Encoding(false));
                    d.blnPronta = true;
                    d.strFase = "pronta";
                }
                actLog(string.Format("discografia de {0} pronta: {1} discos", d.strArtista, d.lstDiscos.Count));
            }
            catch (Exception ex)
            {
                lock (d) d.strErro = "a leitura da discografia falhou: " + ex.Message;
                actLog("discografia: " + ex);
            }
        }

        private static TDiscoDto LerMaster(int intId, string strToken, JavaScriptSerializer jss)
        {
            string strCache = Path.Combine(PastaCache, "master-" + intId + ".json");
            string strJson;
            if (File.Exists(strCache)) strJson = File.ReadAllText(strCache, Encoding.UTF8);
            else
            {
                strJson = Pedido(API + "/masters/" + intId, strToken);
                File.WriteAllText(strCache, strJson, new UTF8Encoding(false));
            }
            Dictionary<string, object> m = jss.Deserialize<Dictionary<string, object>>(strJson);
            TDiscoDto disco = new TDiscoDto
            {
                intMasterId = intId,
                strTitulo = Convert.ToString(m["title"]),
                intAno = m.ContainsKey("year") && m["year"] != null ? Convert.ToInt32(m["year"]) : 0
            };
            if (m.ContainsKey("tracklist") && m["tracklist"] != null)
                foreach (object o in (System.Collections.ArrayList)m["tracklist"])
                {
                    Dictionary<string, object> t = (Dictionary<string, object>)o;
                    string strTipo = t.ContainsKey("type_") ? Convert.ToString(t["type_"]) : "track";
                    if (strTipo != "track") continue;
                    disco.lstTitulos.Add(Convert.ToString(t["title"]));
                    disco.lstDuracoes.Add(Duracao(t.ContainsKey("duration") ? Convert.ToString(t["duration"]) : ""));
                }
            return disco;
        }

        private static int Duracao(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            int intTotal = 0;
            foreach (string p in s.Trim().Split(':'))
            {
                int v;
                if (!int.TryParse(p, out v)) return -1;
                intTotal = intTotal * 60 + v;
            }
            return intTotal > 0 ? intTotal : -1;
        }

        /// <summary>GET autenticado, no ritmo que o Discogs aceita; 429 espera um minuto e tenta de novo.</summary>
        private static string Pedido(string strUrl, string strToken)
        {
            for (int intTentativa = 0; ; intTentativa++)
            {
                lock (s_objTravaRede)
                {
                    TimeSpan espera = INTERVALO - (DateTime.Now - s_dtUltimoPedido);
                    if (espera > TimeSpan.Zero) Thread.Sleep(espera);
                    s_dtUltimoPedido = DateTime.Now;
                }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(strUrl);
                req.Proxy = NetworkConfiguration.CreateProxy();
                req.UserAgent = USER_AGENT;
                req.Headers.Add("Authorization", "Discogs token=" + strToken);
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return r.ReadToEnd();
                }
                catch (WebException ex)
                {
                    HttpWebResponse resp = ex.Response as HttpWebResponse;
                    if (resp != null && (int)resp.StatusCode == 429 && intTentativa < 5) { Thread.Sleep(60000); continue; }
                    if (resp == null && intTentativa < 2) { Thread.Sleep(5000); continue; }
                    throw;
                }
            }
        }

        private static JavaScriptSerializer Serializador()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        private static string Chave(string s)
        {
            return DiscogsClient.NormalizarParaComparacao(s ?? "").Replace(' ', '-');
        }
    }
}
