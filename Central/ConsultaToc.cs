// Identificação pelo ÍNDICE DO CD (WM/MCDI), sem o disco.
//
// O Windows Media Player grava em cada faixa que ripa o índice do CD de origem
// ("D+96+3F51+…": quantas faixas e onde começa cada uma, em setores). É a
// identidade física do disco — mais exata que qualquer busca por nome: acha a
// prensagem certa mesmo com o álbum desconhecido. Duas bases:
//
//   MusicBrainz — pelo Disc ID (SHA-1 do índice); se o ID exato não estiver
//                 cadastrado, pelo índice aproximado (?toc=). Capa pelo Cover
//                 Art Archive. 1 pedido por segundo.
//   GnuDB       — o freedb, como na aquisição de um CD (mesmo ID, mesma
//                 conferência das posições quando a correspondência é inexata).
//
// O índice é lido pelo TTocAudio.DeMcdi (o mesmo da conferência do
// AccurateRip); aqui só se consulta. O Vista recebe os candidatos e a pessoa
// escolhe, como sempre.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using ARCA.Modelo;

namespace ARCA.Central
{
    internal static class ConsultaToc
    {
        private static readonly object s_objTravaMb = new object();
        private static DateTime s_dtUltimoMb = DateTime.MinValue;

        public static TResposta Consultar(string strMcdi, Action<string> actLog)
        {
            TResposta r = new TResposta();
            TTocAudio objToc = TTocAudio.DeMcdi(strMcdi, 0);
            if (objToc == null || objToc.QtdFaixas == 0) { r.strErro = "o índice do CD (WM/MCDI) não se lê"; return r; }
            actLog(string.Format("índice do CD: {0} faixas, {1}", objToc.QtdFaixas, Formato.Tempo((objToc.intLeadOut - objToc.lstInicios[0]) / 75)));

            try
            {
                foreach (TCandidatoIdentificacao c in MusicBrainz(objToc, actLog)) r.lstCandidatos.Add(TCandidatoDto.De(c));
            }
            catch (Exception ex) { actLog("MusicBrainz: " + ex.Message); }
            try
            {
                TCandidatoIdentificacao g = GnuDb(objToc, actLog);
                if (g != null) r.lstCandidatos.Add(TCandidatoDto.De(g));
            }
            catch (Exception ex) { actLog("GnuDB: " + ex.Message); }
            if (r.lstCandidatos.Count == 0) r.strErro = "nem o MusicBrainz nem o GnuDB conhecem este disco pelo índice";
            return r;
        }

        // ================================================================ MusicBrainz

        /// <summary>Disc ID do MusicBrainz: SHA-1 de "primeira, última, lead-out, 99 posições" em hexadecimal, em base64 "de URL".</summary>
        public static string DiscId(TTocAudio t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(t.intPrimeiraFaixa.ToString("X2")).Append((t.intPrimeiraFaixa + t.QtdFaixas - 1).ToString("X2"));
            sb.Append((t.intLeadOut + 150).ToString("X8"));
            for (int i = 0; i < 99; i++) sb.Append((i < t.QtdFaixas ? t.lstInicios[i] + 150 : 0).ToString("X8"));
            using (SHA1 sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()))).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }

        private static string Toc(TTocAudio t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(t.intPrimeiraFaixa).Append('+').Append(t.intPrimeiraFaixa + t.QtdFaixas - 1).Append('+').Append(t.intLeadOut + 150);
            foreach (int i in t.lstInicios) sb.Append('+').Append(i + 150);
            return sb.ToString();
        }

        internal static string BuscarMb(string strUrl)
        {
            lock (s_objTravaMb)
            {
                // 503 é o MusicBrainz pedindo calma: espera e tenta de novo (até 3 vezes).
                for (int intTentativa = 1; ; intTentativa++)
                {
                    TimeSpan espera = TimeSpan.FromMilliseconds(intTentativa == 1 ? 1100 : 2500) - (DateTime.Now - s_dtUltimoMb);
                    if (espera > TimeSpan.Zero) Thread.Sleep(espera);
                    try { return ExternalMetadataClient.FetchString(strUrl); }
                    catch (System.Net.WebException ex)
                    {
                        if (intTentativa >= 3 || NetworkConfiguration.StatusHttp(ex) != 503) throw;
                    }
                    finally { s_dtUltimoMb = DateTime.Now; }
                }
            }
        }

        private static List<TCandidatoIdentificacao> MusicBrainz(TTocAudio objToc, Action<string> actLog)
        {
            List<TCandidatoIdentificacao> lst = new List<TCandidatoIdentificacao>();
            string strId = DiscId(objToc);
            JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> raiz = null;
            bool blnExato = true;
            try
            {
                raiz = jss.Deserialize<Dictionary<string, object>>(BuscarMb("https://musicbrainz.org/ws/2/discid/" + strId +
                    "?inc=artist-credits+recordings+labels&cdstubs=no&fmt=json"));
            }
            catch (Exception ex) { if (NetworkConfiguration.StatusHttp(ex as System.Net.WebException) != 404) throw; }
            ArrayList lstRel = raiz != null ? raiz.ContainsKey("releases") ? (ArrayList)raiz["releases"] : null : null;
            if (lstRel == null || lstRel.Count == 0)
            {
                // ID exato não cadastrado: pelo índice aproximado (mesmas faixas, posições parecidas).
                blnExato = false;
                raiz = jss.Deserialize<Dictionary<string, object>>(BuscarMb("https://musicbrainz.org/ws/2/discid/-?toc=" + Toc(objToc) +
                    "&inc=artist-credits+recordings+labels&cdstubs=no&fmt=json"));
                lstRel = raiz.ContainsKey("releases") ? (ArrayList)raiz["releases"] : null;
            }
            actLog(string.Format("MusicBrainz: disc ID {0} — {1} lançamento(s){2}", strId, lstRel == null ? 0 : lstRel.Count, blnExato ? "" : " pelo índice aproximado"));
            if (lstRel == null) return lst;

            // Até dois lançamentos (prensagens) diferentes, o primeiro com capa.
            foreach (Dictionary<string, object> rel in lstRel.Cast<Dictionary<string, object>>().Take(6))
            {
                Dictionary<string, object> objMeio = MeioDoDisco(rel, strId, objToc.QtdFaixas);
                if (objMeio == null) continue;
                TCandidatoIdentificacao c = CandidatoDe(rel, objMeio, blnExato ? "índice do CD confere" : "índice parecido");
                if (lst.Count == 0 || lst.All(x => x.strDetalhe != c.strDetalhe))
                {
                    c.arrCapa = Capa(Texto(rel, "id"));
                    actLog(string.Format("MusicBrainz: {0} — {1} ({2}){3}", c.strArtista, c.strAlbum, c.strAno, c.arrCapa != null ? ", com capa" : ", sem capa"));
                    lst.Add(c);
                }
                if (lst.Count >= 2) break;
            }
            // Com capa primeiro.
            return lst.OrderBy(x => x.arrCapa == null ? 1 : 0).ToList();
        }

        /// <summary>Um lançamento do MusicBrainz (e o disco dele, se for álbum de vários) como candidato: títulos, intérpretes e durações das faixas, selo, país, código.</summary>
        internal static TCandidatoIdentificacao CandidatoDe(Dictionary<string, object> rel, Dictionary<string, object> objMeio, string strComo)
        {
            TCandidatoIdentificacao c = new TCandidatoIdentificacao { strFonte = "MusicBrainz" };
            c.strAlbum = Texto(rel, "title");
            c.strArtista = Creditos(rel);
            string strData = Texto(rel, "date");
            c.strAno = strData.Length >= 4 ? strData.Substring(0, 4) : null;
            List<string> lstDet = new List<string>();
            ArrayList lstSelo = rel.ContainsKey("label-info") ? rel["label-info"] as ArrayList : null;
            if (lstSelo != null && lstSelo.Count > 0)
            {
                Dictionary<string, object> li = (Dictionary<string, object>)lstSelo[0];
                Dictionary<string, object> lab = li.ContainsKey("label") ? li["label"] as Dictionary<string, object> : null;
                if (lab != null) lstDet.Add(Texto(lab, "name"));
                if (Texto(li, "catalog-number").Length > 0) lstDet.Add(Texto(li, "catalog-number"));
            }
            if (Texto(rel, "country").Length > 0) lstDet.Add(Texto(rel, "country"));
            if (Texto(rel, "barcode").Length > 0) lstDet.Add("código " + Texto(rel, "barcode"));
            ArrayList lstMeios = rel.ContainsKey("media") ? rel["media"] as ArrayList : null;
            if (lstMeios != null && lstMeios.Count > 1) lstDet.Add("disco " + Texto(objMeio, "position") + " de " + lstMeios.Count);
            if (!string.IsNullOrEmpty(strComo)) lstDet.Add(strComo);
            c.strDetalhe = string.Join(" · ", lstDet.ToArray());
            foreach (Dictionary<string, object> t in ((ArrayList)objMeio["tracks"]).Cast<Dictionary<string, object>>())
            {
                Dictionary<string, object> rec = t.ContainsKey("recording") ? t["recording"] as Dictionary<string, object> : null;
                string strTit = Texto(t, "title");
                if (strTit.Length == 0 && rec != null) strTit = Texto(rec, "title");
                c.lstTitulos.Add(strTit);
                string strArt = Creditos(t);
                if (strArt.Length == 0 && rec != null) strArt = Creditos(rec);
                c.lstArtistasFaixas.Add(strArt);
                double ms = Numero(t, "length");
                if (ms <= 0 && rec != null) ms = Numero(rec, "length");
                c.lstDuracoes.Add(ms > 0 ? (int)Math.Round(ms / 1000) : -1);
            }
            return c;
        }

        /// <summary>A capa do lançamento no Cover Art Archive (500 px), ou null.</summary>
        internal static byte[] Capa(string strReleaseId)
        {
            try { return ExternalMetadataClient.FetchImage("https://coverartarchive.org/release/" + strReleaseId + "/front-500"); }
            catch { return null; }
        }

        /// <summary>O disco (medium) do lançamento que é este CD: o que tem o disc ID; senão, o que tem a mesma quantidade de faixas.</summary>
        private static Dictionary<string, object> MeioDoDisco(Dictionary<string, object> rel, string strId, int intFaixas)
        {
            ArrayList lstMeios = rel.ContainsKey("media") ? rel["media"] as ArrayList : null;
            if (lstMeios == null) return null;
            Dictionary<string, object> objPorQtd = null;
            foreach (Dictionary<string, object> m in lstMeios.Cast<Dictionary<string, object>>())
            {
                if (!m.ContainsKey("tracks") || !(m["tracks"] is ArrayList)) continue;
                ArrayList lstDiscos = m.ContainsKey("discs") ? m["discs"] as ArrayList : null;
                if (lstDiscos != null && lstDiscos.Cast<Dictionary<string, object>>().Any(d => Texto(d, "id") == strId)) return m;
                if (objPorQtd == null && ((ArrayList)m["tracks"]).Count == intFaixas) objPorQtd = m;
            }
            return objPorQtd;
        }

        internal static string Creditos(Dictionary<string, object> d)
        {
            ArrayList lst = d.ContainsKey("artist-credit") ? d["artist-credit"] as ArrayList : null;
            if (lst == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (Dictionary<string, object> a in lst.Cast<Dictionary<string, object>>())
            {
                string n = Texto(a, "name");
                if (n.Length == 0 && a.ContainsKey("artist")) n = Texto((Dictionary<string, object>)a["artist"], "name");
                sb.Append(n).Append(Texto(a, "joinphrase"));
            }
            return sb.ToString().Trim();
        }

        internal static string Texto(Dictionary<string, object> d, string k)
        {
            object o; return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "";
        }

        internal static double Numero(Dictionary<string, object> d, string k)
        {
            object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        // ================================================================ GnuDB

        /// <summary>O mesmo que GnuDbResolver.ResolveToc, a partir do índice já montado (lá ele vem de um AudioCD.cue).</summary>
        private static TCandidatoIdentificacao GnuDb(TTocAudio objToc, Action<string> actLog)
        {
            List<int> lstTodas = new List<int>(objToc.lstInicios);
            if (objToc.intDadosAntes >= 0) lstTodas.Insert(0, objToc.intDadosAntes);
            int intFim = objToc.intLeadOut;
            if (objToc.intInicioDados >= 0) { lstTodas.Add(objToc.intInicioDados); intFim = objToc.intLeadOutFisico > 0 ? objToc.intLeadOutFisico : intFim; }
            uint id1, id2, idFreedb;
            AccurateRip.CalcularIds(objToc, out id1, out id2, out idFreedb);
            StringBuilder q = new StringBuilder();
            q.Append(idFreedb.ToString("x8")).Append('+').Append(lstTodas.Count);
            foreach (int intLba in lstTodas) q.Append('+').Append(intLba + 150);
            q.Append('+').Append((intFim + 150) / 75);
            const string HELLO = "&hello=anon+anon.com+ARCA+1.0&proto=6";
            string strQ = ExternalMetadataClient.FetchString("https://gnudb.gnudb.org/~cddb/cddb.cgi?cmd=cddb+query+" + q + HELLO);
            string[] vetQ = (strQ ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (vetQ.Length == 0) return null;
            string strCat = null, strId = null;
            bool blnInexata = vetQ[0].StartsWith("211");
            if (vetQ[0].StartsWith("200")) { string[] p = vetQ[0].Split(' '); if (p.Length >= 3) { strCat = p[1]; strId = p[2]; } }
            else if ((vetQ[0].StartsWith("210") || blnInexata) && vetQ.Length > 1 && !vetQ[1].StartsWith("."))
            { string[] p = vetQ[1].Split(' '); if (p.Length >= 2) { strCat = p[0]; strId = p[1]; } }
            if (strCat == null) { actLog("GnuDB: o disco não está cadastrado (" + vetQ[0] + ")"); return null; }

            string strR = ExternalMetadataClient.FetchString("https://gnudb.gnudb.org/~cddb/cddb.cgi?cmd=cddb+read+" + strCat + "+" + strId + HELLO);
            string[] vetR = (strR ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (blnInexata)
            {
                List<int> lstReg = new List<int>(); bool blnLista = false;
                foreach (string l in vetR)
                {
                    if (l.StartsWith("# Track frame offsets", StringComparison.OrdinalIgnoreCase)) { blnLista = true; continue; }
                    if (!blnLista) continue;
                    Match m = Regex.Match(l, @"^#\s*(\d+)\s*$");
                    if (m.Success) lstReg.Add(int.Parse(m.Groups[1].Value)); else if (lstReg.Count > 0) break;
                }
                bool blnConfere = lstReg.Count == lstTodas.Count;
                for (int i = 0; blnConfere && i < lstReg.Count; i++) if (Math.Abs(lstReg[i] - (lstTodas[i] + 150)) > 150) blnConfere = false;
                if (!blnConfere) { actLog("GnuDB: só achou um disco parecido; não é este"); return null; }
            }
            StringBuilder sbTitulo = new StringBuilder();
            Dictionary<int, string> dicFaixas = new Dictionary<int, string>();
            string strAno = null, strGenero = null;
            foreach (string l in vetR)
            {
                if (l.StartsWith("DTITLE=")) sbTitulo.Append(l.Substring(7));
                else if (l.StartsWith("DYEAR=")) strAno = l.Substring(6).Trim();
                else if (l.StartsWith("DGENRE=")) strGenero = l.Substring(7).Trim();
                else
                {
                    Match m = Regex.Match(l, @"^TTITLE(\d+)=(.*)");
                    if (m.Success) { int k = int.Parse(m.Groups[1].Value); string a; dicFaixas[k] = dicFaixas.TryGetValue(k, out a) ? a + m.Groups[2].Value : m.Groups[2].Value; }
                }
            }
            string strDT = sbTitulo.ToString();
            int intBarra = strDT.IndexOf(" / ", StringComparison.Ordinal);
            TCandidatoIdentificacao c = new TCandidatoIdentificacao
            {
                strFonte = "GnuDB",
                strArtista = intBarra >= 0 ? strDT.Substring(0, intBarra).Trim() : "",
                strAlbum = (intBarra >= 0 ? strDT.Substring(intBarra + 3) : strDT).Trim(),
                strAno = string.IsNullOrEmpty(strAno) ? null : strAno,
                strGenero = string.IsNullOrEmpty(strGenero) ? null : strGenero,
                strDetalhe = "freedb " + strCat + "/" + strId + (blnInexata ? " · índice parecido" : " · índice do CD confere")
            };
            int intOffDados = objToc.intDadosAntes >= 0 ? 1 : 0;   // mixed-mode: a faixa 0 do freedb é a de dados
            for (int i = 0; i < objToc.QtdFaixas; i++)
            {
                string t; dicFaixas.TryGetValue(i + intOffDados, out t);
                t = (t ?? "").Trim();
                if (c.strArtista.Length > 0)
                    foreach (string sep in new[] { " / ", " - " })
                        if (t.StartsWith(c.strArtista + sep, StringComparison.OrdinalIgnoreCase)) t = t.Substring(c.strArtista.Length + sep.Length).Trim();
                c.lstTitulos.Add(t.Length > 0 ? t : "Faixa " + (i + 1));
                c.lstArtistasFaixas.Add("");
                c.lstDuracoes.Add((objToc.FimDaFaixa(i) - objToc.lstInicios[i]) / 75);
            }
            // Registro vazio (há muitos no freedb, sobretudo na categoria "data"): sem álbum ou sem nenhum título de faixa não serve.
            if (c.strAlbum.Length == 0 || c.lstTitulos.All(t => Regex.IsMatch(t, @"^(Faixa|Track)\s*\d+$", RegexOptions.IgnoreCase)))
            {
                actLog("GnuDB: o registro " + strCat + "/" + strId + " está vazio (sem álbum ou sem títulos); descartado");
                return null;
            }
            actLog(string.Format("GnuDB: {0} — {1} ({2})", c.strArtista, c.strAlbum, c.strAno));
            return c;
        }
    }
}
