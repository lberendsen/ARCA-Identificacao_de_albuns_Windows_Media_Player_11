// Busca de UMA música no Apple Music, pelo título (e o artista, se houver).
//
// Serve à separação do "Álbum Desconhecido": faixa solta, sem álbum, cujo
// único indício é o nome do arquivo. A busca devolve as músicas candidatas;
// quem decide qual é (título, duração, artista) é o Vista, na janela.
//
// A API de busca do iTunes aceita uns 20 pedidos por minuto. Por isso há um
// intervalo de 3,2 s entre pedidos à rede, e cada termo fica em cache no
// disco do notebook: repetir a separação, ou retomar uma interrompida, é
// instantâneo.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using ARCA.Modelo;

namespace ARCA.Central
{
    internal static class BuscaFaixa
    {
        private static readonly TimeSpan INTERVALO = TimeSpan.FromMilliseconds(3200);
        private static readonly object s_objTravaRede = new object();
        private static DateTime s_dtUltimo = DateTime.MinValue;

        private static string PastaCache
        {
            get
            {
                string s = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache-itunes-faixas");
                Directory.CreateDirectory(s);
                return s;
            }
        }

        public static TBuscaFaixaDto Buscar(string strArtista, string strTitulo, Action<string> actLog)
        {
            string strTermo = ((strArtista ?? "").Trim() + " " + (strTitulo ?? "").Trim()).Trim();
            TBuscaFaixaDto r = new TBuscaFaixaDto { strTermo = strTermo };
            if (strTermo.Length < 2) { r.strErro = "termo vazio"; return r; }

            JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string strCache = Path.Combine(PastaCache, Chave(strTermo) + ".json");
            if (File.Exists(strCache))
            {
                try
                {
                    TBuscaFaixaDto c = jss.Deserialize<TBuscaFaixaDto>(File.ReadAllText(strCache, Encoding.UTF8));
                    c.blnDoCache = true;
                    return c;
                }
                catch { }
            }

            string strJson;
            lock (s_objTravaRede)
            {
                TimeSpan espera = INTERVALO - (DateTime.Now - s_dtUltimo);
                if (espera > TimeSpan.Zero) Thread.Sleep(espera);
                try
                {
                    strJson = ExternalMetadataClient.FetchString("https://itunes.apple.com/search?entity=song&limit=25&country=BR&term=" + Uri.EscapeDataString(strTermo));
                }
                catch (Exception ex)
                {
                    s_dtUltimo = DateTime.Now;
                    r.strErro = "Apple Music: " + ex.Message;
                    actLog("faixa '" + strTermo + "': " + ex.Message);
                    return r;   // erro não vai para o cache
                }
                s_dtUltimo = DateTime.Now;
            }

            Dictionary<string, object> raiz = jss.Deserialize<Dictionary<string, object>>(strJson);
            object objResultados;
            if (raiz != null && raiz.TryGetValue("results", out objResultados) && objResultados is ArrayList)
            {
                foreach (object o in (ArrayList)objResultados)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null || Texto(d, "wrapperType") != "track" || Texto(d, "kind") != "song") continue;
                    string strData = Texto(d, "releaseDate");
                    int intAno = 0;
                    if (strData.Length >= 4) int.TryParse(strData.Substring(0, 4), out intAno);
                    r.lstFaixas.Add(new TFaixaAppleDto
                    {
                        strArtista = Texto(d, "artistName"),
                        strAlbum = Texto(d, "collectionName"),
                        strArtistaAlbum = Texto(d, "collectionArtistName").Length > 0 ? Texto(d, "collectionArtistName") : Texto(d, "artistName"),
                        strTitulo = Texto(d, "trackName"),
                        intFaixa = Inteiro(d, "trackNumber"),
                        intTotalFaixas = Inteiro(d, "trackCount"),
                        intDisco = Inteiro(d, "discNumber"),
                        intTotalDiscos = Inteiro(d, "discCount"),
                        intAno = intAno,
                        intDuracao = (int)Math.Round(Inteiro(d, "trackTimeMillis") / 1000.0),
                        strGenero = Texto(d, "primaryGenreName"),
                        lngAlbumId = (long)Numero(d, "collectionId"),
                        strLinkAlbum = Texto(d, "collectionViewUrl")
                    });
                }
            }
            try { File.WriteAllText(strCache, jss.Serialize(r), Encoding.UTF8); } catch { }
            return r;
        }

        private static string Texto(Dictionary<string, object> d, string k)
        {
            object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        private static double Numero(Dictionary<string, object> d, string k)
        {
            object o; if (!d.TryGetValue(k, out o) || o == null) return 0;
            try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static int Inteiro(Dictionary<string, object> d, string k) { return (int)Numero(d, k); }

        private static string Chave(string s)
        {
            using (SHA1 sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
