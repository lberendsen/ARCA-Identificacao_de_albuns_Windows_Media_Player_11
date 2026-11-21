// Identificação pelo SOM (AcoustID), para o disco que não tem nome, nem link,
// nem índice do CD: "Faixa 1, Faixa 2…" ripadas sem informação.
//
// Para cada faixa, o fpcalc (Chromaprint) tira a impressão digital do áudio
// — lendo o arquivo do Vista pelo compartilhamento, sem copiar — e o AcoustID
// diz que gravação é. Só a impressão sai do notebook, nunca o áudio. As
// gravações apontam para lançamentos do MusicBrainz; o lançamento (e o disco
// dele) em que caem mais faixas é o álbum. Títulos, durações e capa vêm do
// MusicBrainz, como na identificação pelo índice do CD.
//
// A chave do AcoustID (de aplicação, https://acoustid.org/new-application) fica em
// acoustid.chave, ao lado do executável. O AcoustID aceita 3 pedidos por
// segundo; o MusicBrainz, 1.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using ARCA.Modelo;

namespace ARCA.Central
{
    internal static class BuscaSom
    {
        private static string Pasta { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>O caminho da máquina do WMP (D:\…) como este computador o alcança (\\cliente\D$\…).</summary>
        private static string NoNotebook(string s)
        {
            if (s.Length >= 3 && s[1] == ':' && s[2] == '\\') return @"\\" + Programa.Rede.Cliente + @"\" + char.ToUpperInvariant(s[0]) + "$" + s.Substring(2);
            return s;
        }

        private sealed class TVoto
        {
            public string strRelease, strGrupo, strTitulo, strPais;
            public int intMeio, intFaixasNoMeio;
            public HashSet<int> setArquivos = new HashSet<int>();
        }

        public static TResposta Consultar(List<string> lstCaminhos, Action<string> actLog)
        {
            TResposta r = new TResposta();
            string strFpcalc = Path.Combine(Pasta, "fpcalc.exe");
            string strArqChave = Path.Combine(Pasta, "acoustid.chave");
            if (!File.Exists(strFpcalc)) { r.strErro = "falta o fpcalc.exe ao lado do ARCA Central"; return r; }
            if (!File.Exists(strArqChave)) { r.strErro = "falta a chave do AcoustID (acoustid.chave)"; return r; }
            string strChave = File.ReadAllText(strArqChave).Trim();
            if (lstCaminhos == null || lstCaminhos.Count == 0) { r.strErro = "nenhuma faixa"; return r; }

            JavaScriptSerializer jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, TVoto> dicVotos = new Dictionary<string, TVoto>();
            int intReconhecidas = 0;
            for (int i = 0; i < lstCaminhos.Count; i++)
            {
                string strArq = Path.GetFileName(lstCaminhos[i]);
                int intDur; string strFp;
                try { Impressao(strFpcalc, NoNotebook(lstCaminhos[i]), out intDur, out strFp); }
                catch (Exception ex) { actLog("som: " + strArq + ": " + ex.Message); continue; }

                Dictionary<string, object> raiz;
                try
                {
                    string strCorpo = "client=" + Uri.EscapeDataString(strChave) + "&duration=" + intDur + "&format=json" +
                                      "&meta=recordings+releasegroups+releases+tracks&fingerprint=" + Uri.EscapeDataString(strFp);
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                        wc.Headers[HttpRequestHeader.UserAgent] = "ARCA-Identificacao/1.0 ( https://github.com/lberendsen/ARCA-Identificacao_de_albuns_Windows_Media_Player_11 )";
                        wc.Encoding = Encoding.UTF8;
                        raiz = jss.Deserialize<Dictionary<string, object>>(wc.UploadString("https://api.acoustid.org/v2/lookup", strCorpo));
                    }
                }
                catch (Exception ex) { actLog("som: " + strArq + ": AcoustID: " + ex.Message); continue; }
                Thread.Sleep(350);   // até 3 por segundo

                bool blnAchou = false;
                ArrayList lstRes = raiz.ContainsKey("results") ? raiz["results"] as ArrayList : null;
                if (lstRes != null)
                    foreach (Dictionary<string, object> res in lstRes.Cast<Dictionary<string, object>>())
                    {
                        if (ConsultaToc.Numero(res, "score") < 0.7) continue;
                        ArrayList lstRec = res.ContainsKey("recordings") ? res["recordings"] as ArrayList : null;
                        if (lstRec == null) continue;
                        foreach (Dictionary<string, object> rec in lstRec.Cast<Dictionary<string, object>>())
                        {
                            // meta com releasegroups: recordings[].releasegroups[].releases[]; sem: recordings[].releases[]
                            List<KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>> lstRel = new List<KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>>();
                            if (rec.ContainsKey("releasegroups"))
                                foreach (Dictionary<string, object> rg in ((ArrayList)rec["releasegroups"]).Cast<Dictionary<string, object>>())
                                    if (rg.ContainsKey("releases"))
                                        foreach (Dictionary<string, object> rl in ((ArrayList)rg["releases"]).Cast<Dictionary<string, object>>())
                                            lstRel.Add(new KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>(rg, rl));
                            if (rec.ContainsKey("releases"))
                                foreach (Dictionary<string, object> rl in ((ArrayList)rec["releases"]).Cast<Dictionary<string, object>>())
                                    lstRel.Add(new KeyValuePair<Dictionary<string, object>, Dictionary<string, object>>(null, rl));
                            foreach (var kv in lstRel)
                            {
                                Dictionary<string, object> rl = kv.Value;
                                string strRel = ConsultaToc.Texto(rl, "id");
                                ArrayList lstMeios = rl.ContainsKey("mediums") ? rl["mediums"] as ArrayList : null;
                                int intMeio = 1, intQtd = 0;
                                if (lstMeios != null && lstMeios.Count > 0)
                                {
                                    Dictionary<string, object> m = (Dictionary<string, object>)lstMeios[0];
                                    intMeio = (int)ConsultaToc.Numero(m, "position"); if (intMeio <= 0) intMeio = 1;
                                    intQtd = (int)ConsultaToc.Numero(m, "track_count");
                                }
                                string k = strRel + "#" + intMeio;
                                TVoto v;
                                if (!dicVotos.TryGetValue(k, out v))
                                    dicVotos[k] = v = new TVoto
                                    {
                                        strRelease = strRel, intMeio = intMeio, intFaixasNoMeio = intQtd,
                                        strGrupo = kv.Key != null ? ConsultaToc.Texto(kv.Key, "id") : strRel,
                                        strTitulo = ConsultaToc.Texto(rl, "title").Length > 0 ? ConsultaToc.Texto(rl, "title") : (kv.Key != null ? ConsultaToc.Texto(kv.Key, "title") : ""),
                                        strPais = ConsultaToc.Texto(rl, "country")
                                    };
                                v.setArquivos.Add(i);
                                blnAchou = true;
                            }
                        }
                    }
                if (blnAchou) intReconhecidas++;
                actLog("som: " + strArq + (blnAchou ? ": reconhecida" : ": o AcoustID não conhece"));
            }
            actLog(string.Format("som: {0} de {1} faixa(s) reconhecida(s)", intReconhecidas, lstCaminhos.Count));
            if (dicVotos.Count == 0) { r.strErro = "o AcoustID não reconheceu nenhuma faixa pelo som"; return r; }

            // Por álbum (grupo de lançamentos): o que reúne mais faixas; dentro dele, o lançamento com mais
            // faixas e, no empate, o que tem a quantidade de faixas mais perto da que foi aberta (o CD, não a coletânea dupla).
            // No empate, o álbum avulso vence a caixa/coletânea dupla ("Play / 18").
            var lstGrupos = dicVotos.Values.GroupBy(v => v.strGrupo)
                .Select(g => g.OrderByDescending(v => v.setArquivos.Count)
                              .ThenBy(v => v.strTitulo.Contains(" / ") ? 1 : 0)
                              .ThenBy(v => Math.Abs((v.intFaixasNoMeio > 0 ? v.intFaixasNoMeio : 99) - lstCaminhos.Count))
                              .ThenBy(v => v.strPais == "BR" ? 0 : v.strPais == "XW" ? 1 : 2).First())
                .OrderByDescending(v => v.setArquivos.Count)
                .ThenBy(v => v.strTitulo.Contains(" / ") ? 1 : 0)
                .ThenBy(v => Math.Abs((v.intFaixasNoMeio > 0 ? v.intFaixasNoMeio : 99) - lstCaminhos.Count))
                .Take(2).ToList();
            foreach (TVoto v in lstGrupos)
            {
                if (v.setArquivos.Count < Math.Max(1, (lstCaminhos.Count + 2) / 3) && r.lstCandidatos.Count > 0) break;   // segundo lugar fraco não vale a coluna
                try
                {
                    Dictionary<string, object> rel = jss.Deserialize<Dictionary<string, object>>(ConsultaToc.BuscarMb(
                        "https://musicbrainz.org/ws/2/release/" + v.strRelease + "?inc=recordings+artist-credits+labels&fmt=json"));
                    ArrayList lstMeios = rel.ContainsKey("media") ? rel["media"] as ArrayList : null;
                    Dictionary<string, object> objMeio = lstMeios == null ? null
                        : lstMeios.Cast<Dictionary<string, object>>().FirstOrDefault(m => (int)ConsultaToc.Numero(m, "position") == v.intMeio)
                          ?? lstMeios.Cast<Dictionary<string, object>>().FirstOrDefault();
                    if (objMeio == null || !(objMeio["tracks"] is ArrayList)) continue;
                    TCandidatoIdentificacao c = ConsultaToc.CandidatoDe(rel, objMeio, string.Format("pelo som: {0} de {1} faixa(s)", v.setArquivos.Count, lstCaminhos.Count));
                    c.arrCapa = ConsultaToc.Capa(v.strRelease);
                    actLog(string.Format("som: {0} — {1} ({2}): {3} de {4} faixa(s){5}", c.strArtista, c.strAlbum, c.strAno, v.setArquivos.Count, lstCaminhos.Count, c.arrCapa != null ? ", com capa" : ""));
                    r.lstCandidatos.Add(TCandidatoDto.De(c));
                }
                catch (Exception ex) { actLog("som: MusicBrainz: " + ex.Message); }
            }
            if (r.lstCandidatos.Count == 0) r.strErro = "o som foi reconhecido, mas o MusicBrainz não devolveu o álbum";
            return r;
        }

        private static void Impressao(string strFpcalc, string strArquivo, out int intDur, out string strFp)
        {
            ProcessStartInfo psi = new ProcessStartInfo(strFpcalc, "-json \"" + strArquivo + "\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (Process p = Process.Start(psi))
            {
                string strSaida = p.StandardOutput.ReadToEnd();
                string strErro = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new TimeoutException("o fpcalc não terminou"); }
                if (p.ExitCode != 0) throw new Exception("fpcalc: " + strErro.Trim());
                Dictionary<string, object> j = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(strSaida);
                intDur = (int)Math.Round(ConsultaToc.Numero(j, "duration"));
                strFp = ConsultaToc.Texto(j, "fingerprint");
            }
        }
    }
}
