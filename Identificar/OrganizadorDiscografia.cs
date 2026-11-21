using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ARCA.Central;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma linha da grade: a faixa como está e o disco que a discografia propõe.</summary>
    public sealed class TLinhaDisco : INotifyPropertyChanged
    {
        public sealed class TNaoMexer { public override string ToString() { return "— não mexer nesta faixa"; } }
        public static readonly TNaoMexer NAO_MEXER = new TNaoMexer();

        public event PropertyChangedEventHandler PropertyChanged;
        private void Avisar(params string[] v) { if (PropertyChanged != null) foreach (string s in v) PropertyChanged(this, new PropertyChangedEventArgs(s)); }

        public TFaixaMp3 objFaixa { get; set; }
        public TFaixaEntrada objEntrada { get; set; }
        public List<object> lstOpcoes { get; set; }

        public string strPasta { get { return Path.GetFileName(Path.GetDirectoryName(objFaixa.strCaminho)); } }
        public string strArquivo { get { return objFaixa.strArquivo; } }
        public string strTituloAtual { get { return objFaixa.strTitulo; } }
        public string strAnoAtual { get { return objEntrada.intAno > 0 ? objEntrada.intAno.ToString() : ""; } }
        public string strDuracao { get { return objFaixa.strDuracao; } }

        private object m_objEscolhida = NAO_MEXER;
        public object objEscolhida
        {
            get { return m_objEscolhida; }
            set
            {
                m_objEscolhida = value ?? NAO_MEXER;
                blnAplicar = m_objEscolhida is TOpcaoDisco;
                Avisar("objEscolhida", "strDisco", "strNumero", "strTituloNovo", "strDif", "strConfianca", "objCorConfianca");
            }
        }
        public TOpcaoDisco objOpcao { get { return m_objEscolhida as TOpcaoDisco; } }

        private bool m_blnAplicar;
        public bool blnAplicar { get { return m_blnAplicar; } set { m_blnAplicar = value && objOpcao != null; Avisar("blnAplicar"); } }

        public string strDisco { get { return objOpcao == null ? "—" : objOpcao.objDisco.intAno + " · " + objOpcao.objDisco.strTitulo; } }
        public string strNumero { get { return objOpcao == null ? "" : (objOpcao.intPosicao + 1).ToString(); } }
        public string strTituloNovo { get { return objOpcao == null ? "" : OrganizadorDiscografia.SemTraducao(objOpcao.strTituloNoDisco); } }
        public string strDif
        {
            get
            {
                if (objOpcao == null || objOpcao.intDifDuracao == int.MinValue) return "";
                int d = objOpcao.intDifDuracao;
                return (d > 0 ? "+" : d < 0 ? "−" : "") + Math.Abs(d) + " s";
            }
        }
        public string strConfianca { get { return CasadorDiscografia.Confianca(objEntrada, objOpcao); } }
        private static readonly Brush COR_ALTA = Congelado(0x1E, 0x7B, 0x34), COR_MEDIA = Congelado(0xA0, 0x60, 0x00), COR_BAIXA = Congelado(0xC0, 0x1C, 0x1C);
        private static Brush Congelado(byte r, byte g, byte b) { SolidColorBrush s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
        public Brush objCorConfianca
        {
            get
            {
                // Tons escuros: legíveis no fundo branco do tema do Vista.
                string c = strConfianca;
                return c == "alta" ? COR_ALTA : c == "média" ? COR_MEDIA : c == "baixa" ? COR_BAIXA : Brushes.Gray;
            }
        }

        private string m_strResultado;
        public string strResultado { get { return m_strResultado; } set { m_strResultado = value; Avisar("strResultado"); } }
    }

    /// <summary>
    /// A parte da organização pela discografia que não é tela: nome do álbum,
    /// nome da pasta e a gravação de um disco. Separada da JanelaDiscografia
    /// (que virou XAML) para a sonda S8 compilar sem janela nenhuma.
    /// </summary>
    public static class OrganizadorDiscografia
    {
        /// <summary>O nome do álbum: o do disco; com o ano, se o artista tem mais de um disco com esse nome.</summary>
        public static string NomeDoAlbum(TDiscoDto d, TDiscografiaDto objDiscografia)
        {
            string n = DiscogsClient.NormalizarParaComparacao(d.strTitulo);
            bool blnRepetido = objDiscografia.lstDiscos.Count(x => DiscogsClient.NormalizarParaComparacao(x.strTitulo) == n) > 1;
            return blnRepetido && d.intAno > 0 ? d.strTitulo.Trim() + " (" + d.intAno + ")" : d.strTitulo.Trim();
        }

        /// <summary>"Esqueça = Forget Him" → "Esqueça": o Discogs junta a tradução ao título.</summary>
        public static string SemTraducao(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf(" = ", StringComparison.Ordinal);
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        public static string NomeDePasta(string s)
        {
            s = Regex.Replace(s, @"\s*[/\\:|]+\s*", " - ");   // "Chill:Brazil 4" -> "Chill - Brazil 4"
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        }

        /// <summary>Um disco: o lançamento principal do master (títulos com acento e capa) e a gravação das faixas dele.</summary>
        public static int GravarDisco(TDiscoDto d, string strAlbum, List<TLinhaDisco> lst, string strArtista, bool blnCapas, bool blnMover,
                                      Dictionary<string, HashSet<int>> dicDiscosDaPasta, Action<string> actLog)
        {
            actLog(string.Format("== {0} · {1} ({2} faixas) — {3}", d.intAno, strAlbum, lst.Count, d.Link()));
            TCandidatoIdentificacao c = null;
            try
            {
                TResposta r = ClienteCentral.PorLink(d.Link(), d.lstTitulos.Count);
                if (r.objCandidato != null) c = r.objCandidato.ParaCandidato();
                else actLog("   o lançamento principal não veio (" + r.strErro + "); uso as faixas do master, sem capa");
            }
            catch (Exception ex) { actLog("   o lançamento principal não veio (" + ex.Message + "); uso as faixas do master, sem capa"); }

            List<string> lstTitulos = c != null && c.lstTitulos.Count > 0 ? c.lstTitulos : d.lstTitulos;
            foreach (TLinhaDisco l in lst)
            {
                TFaixaMp3 f = l.objFaixa;
                // a posição no lançamento principal: a mesma do master se as listas batem; senão pelo título
                int j = l.objOpcao.intPosicao;
                if (lstTitulos.Count != d.lstTitulos.Count || j >= lstTitulos.Count
                    || CasadorDiscografia.Similaridade(SemTraducao(lstTitulos[j]), SemTraducao(l.objOpcao.strTituloNoDisco)) < 0.8)
                {
                    double dblMelhor = -1;
                    for (int k = 0; k < lstTitulos.Count; k++)
                    {
                        double s = CasadorDiscografia.Similaridade(SemTraducao(lstTitulos[k]), SemTraducao(l.objOpcao.strTituloNoDisco));
                        if (s > dblMelhor) { dblMelhor = s; j = k; }
                    }
                }
                f.intPosicaoNova = j;
                f.strTituloNovo = SemTraducao(lstTitulos[j]);
                string strArtFaixa = c != null && j < c.lstArtistasFaixas.Count ? c.lstArtistasFaixas[j] : "";
                f.strArtistaNovo = string.Equals((strArtFaixa ?? "").Trim(), strArtista.Trim(), StringComparison.OrdinalIgnoreCase) ? "" : strArtFaixa;
                f.strCompositorNovo = c != null && c.lstCompositores != null && j < c.lstCompositores.Count ? c.lstCompositores[j] : "";
                f.blnAplicar = true;
            }

            string strGenero = c == null ? "" : ((c.strGenero ?? "").Split(',').Select(s => s.Trim()).FirstOrDefault() ?? "");
            TDadosAlbum a = new TDadosAlbum
            {
                strArtistaAlbum = strArtista,
                strAlbum = strAlbum,
                strAno = d.intAno > 0 ? d.intAno.ToString() : "",
                strGenero = strGenero,
                intTotalFaixas = lstTitulos.Count,
                strFonte = "Discogs master " + d.intMasterId + (c != null && !string.IsNullOrEmpty(c.strDetalhe) ? " · " + c.strDetalhe : ""),
                arrCapa = blnCapas && c != null ? c.arrCapa : null,
                strOrigemCapa = "Discogs",
                strEvento = "organização pela discografia (ARCA Identificar)"
            };

            int intOk = 0;
            // grava por pasta de origem: a decisão sobre Folder.jpg é de cada pasta
            foreach (IGrouping<string, TLinhaDisco> g in lst.GroupBy(l => Path.GetDirectoryName(l.objFaixa.strCaminho), StringComparer.OrdinalIgnoreCase))
            {
                string strDestino = null;
                if (blnMover)
                {
                    string strPai = Path.GetDirectoryName(g.Key);
                    strDestino = Path.Combine(strPai, NomeDePasta((d.intAno > 0 ? d.intAno + " - " : "") + strAlbum));
                }
                bool blnImagens = blnMover || dicDiscosDaPasta[g.Key].Count == 1;
                List<TFaixaMp3> lstF = g.Select(l => l.objFaixa).ToList();
                intOk += GravadorMp3.Aplicar(lstF, a, actLog, delegate { }, strDestino, blnImagens);
                foreach (TLinhaDisco l in g) l.strResultado = l.objFaixa.strResultado;
                if (strDestino != null)
                {
                    try { GravadorMp3.LimparPastaEsvaziada(g.Key, strDestino, actLog); }
                    catch (Exception ex) { actLog("   a pasta antiga ficou como estava: " + ex.Message); }
                }
            }
            return intOk;
        }
    }
}
