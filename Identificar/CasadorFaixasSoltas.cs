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
    /// <summary>Uma possibilidade para uma faixa solta: uma música do Apple Music, com a nota do casamento.</summary>
    public sealed class TOpcaoApple
    {
        public TFaixaAppleDto objApple;
        public double dblPontos;
        public double dblSimTitulo;
        public double dblSimArtista = -1;   // -1 = a faixa não tinha artista para comparar
        public int intDif = int.MinValue;   // segundos (Apple − arquivo)

        public override string ToString()
        {
            TFaixaAppleDto a = objApple;
            return a.strArtistaAlbum + " — " + a.strAlbum + (a.intAno > 0 ? " (" + a.intAno + ")" : "") +
                   "  ·  " + a.intFaixa + ". " + a.strTitulo + (intDif != int.MinValue ? "  [" + (intDif >= 0 ? "+" : "−") + Math.Abs(intDif) + " s]" : "");
        }
    }

    /// <summary>Uma linha da separação: a faixa como está, o que dá para tirar do nome, e o que a busca propõe.</summary>
    public sealed class TFaixaSolta : INotifyPropertyChanged
    {
        public sealed class TNaoMexer { public override string ToString() { return "— não mexer nesta faixa"; } }
        public static readonly TNaoMexer NAO_MEXER = new TNaoMexer();

        public event PropertyChangedEventHandler PropertyChanged;
        private void Avisar(params string[] v) { if (PropertyChanged != null) foreach (string s in v) PropertyChanged(this, new PropertyChangedEventArgs(s)); }

        private TFaixaMp3 m_objFaixa;
        public TFaixaMp3 objFaixa
        {
            get { return m_objFaixa; }
            set { m_objFaixa = value; if (strPastaOrigem == null && value != null) strPastaOrigem = Path.GetDirectoryName(value.strCaminho); }
        }
        /// <summary>A pasta onde a faixa estava ao abrir a janela (depois de movida, objFaixa.strCaminho muda).</summary>
        public string strPastaOrigem { get; private set; }
        public string strArquivo { get { return objFaixa.strArquivo; } }
        public string strDuracao { get { return objFaixa.strDuracao; } }
        public string strIndicio { get; set; }

        private string m_strTitulo, m_strArtista;
        /// <summary>O título que vai para a busca (editável na grade).</summary>
        public string strTituloBusca { get { return m_strTitulo; } set { m_strTitulo = value; Avisar("strTituloBusca"); } }
        /// <summary>O artista que vai para a busca, se houver (editável na grade).</summary>
        public string strArtistaBusca { get { return m_strArtista; } set { m_strArtista = value; Avisar("strArtistaBusca"); } }

        public bool blnBuscada { get; set; }
        private List<object> m_lstOpcoes = new List<object> { NAO_MEXER };
        public List<object> lstOpcoes { get { return m_lstOpcoes; } set { m_lstOpcoes = value; Avisar("lstOpcoes"); } }

        private object m_objEscolhida = NAO_MEXER;
        public object objEscolhida
        {
            get { return m_objEscolhida; }
            set
            {
                m_objEscolhida = value ?? NAO_MEXER;
                blnAplicar = m_objEscolhida is TOpcaoApple;
                Avisar("objEscolhida", "strDisco", "strNumero", "strTituloNovo", "strDif", "strConfianca", "objCorConfianca");
            }
        }
        public TOpcaoApple objOpcao { get { return m_objEscolhida as TOpcaoApple; } }

        private bool m_blnAplicar;
        public bool blnAplicar { get { return m_blnAplicar; } set { m_blnAplicar = value && objOpcao != null; Avisar("blnAplicar"); } }

        public string strDisco
        {
            get
            {
                if (objOpcao == null) return blnBuscada ? "— nada que confira" : "";
                TFaixaAppleDto a = objOpcao.objApple;
                return a.strArtistaAlbum + " — " + a.strAlbum + (a.intAno > 0 ? " (" + a.intAno + ")" : "");
            }
        }
        public string strNumero { get { return objOpcao == null ? "" : objOpcao.objApple.intFaixa.ToString(); } }
        public string strTituloNovo { get { return objOpcao == null ? "" : objOpcao.objApple.strTitulo; } }
        public string strDif
        {
            get
            {
                if (objOpcao == null || objOpcao.intDif == int.MinValue) return "";
                int d = objOpcao.intDif;
                return (d > 0 ? "+" : d < 0 ? "−" : "") + Math.Abs(d) + " s";
            }
        }
        public string strConfianca { get { return CasadorFaixasSoltas.Confianca(this, objOpcao); } }
        private static readonly Brush COR_ALTA = Congelado(0x1E, 0x7B, 0x34), COR_MEDIA = Congelado(0xA0, 0x60, 0x00), COR_BAIXA = Congelado(0xC0, 0x1C, 0x1C);
        private static Brush Congelado(byte r, byte g, byte b) { SolidColorBrush s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
        public Brush objCorConfianca
        {
            get { string c = strConfianca; return c == "alta" ? COR_ALTA : c == "média" ? COR_MEDIA : c == "baixa" ? COR_BAIXA : Brushes.Gray; }
        }

        private string m_strResultado;
        public string strResultado { get { return m_strResultado; } set { m_strResultado = value; Avisar("strResultado"); } }
    }

    /// <summary>
    /// A separação do "Álbum Desconhecido": faixas soltas, sem álbum, muitas sem
    /// tag nenhuma. O que se sabe de cada uma é o nome do arquivo (às vezes
    /// truncado) e a duração. A busca no Apple Music é por título; o casamento
    /// pesa título, duração e artista, e depois puxa para o mesmo disco as
    /// faixas que podem ser dele (um álbum de estúdio que aparece para várias
    /// faixas vence a coletânea que só aparece para uma).
    /// </summary>
    public static class CasadorFaixasSoltas
    {
        private static readonly Regex s_rxGenerico = new Regex(@"^(faixa( desconhecida)?|track|song|unknown track|faixa de audio)\s*[-_ ]*\d*$", RegexOptions.IgnoreCase);
        private static readonly Regex s_rxObra = new Regex(@"(^[IVX]+\.\s)|\b(sonata|op\.?|bwv|k\.?\s?\d|symphon|sinfonia|concerto|quartet|quintet|klavier|allegro|andante|adagio|presto|moderato|finale|scherzo|largo|vivace|rondo|menuet|movement|ouvert|cd\s?\d)", RegexOptions.IgnoreCase);
        private static readonly Regex s_rxVersao = new Regex(@"\b(ao vivo|live|karaok|tribut|cover|lullaby|bebê|bebe|ringtone|instrumental|remix|8-bit|piano version)\b", RegexOptions.IgnoreCase);

        /// <summary>Título, artista e indício (grupo) tirados da tag e do nome do arquivo.</summary>
        public static void Preparar(TFaixaSolta l)
        {
            TFaixaMp3 f = l.objFaixa;
            string strNome = Path.GetFileNameWithoutExtension(f.strCaminho);
            string s = Regex.Replace(strNome, @"_", " ");
            s = Regex.Replace(s, @"\(\d+ BPM\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"^\s*(cd\s?\d+\s+)?((\d{1,3}|\d-\d{2})[\s.\-]+)+", "", RegexOptions.IgnoreCase);   // "cd2 08 ", "01 01 ", "2-06 ", "05-"
            s = Regex.Replace(s, @"\s+\d{1,2}$", "");                    // " 2" da cópia
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', '%');

            string strTitulo = (f.strTitulo ?? "").Trim(), strArtista = (f.strArtista ?? "").Trim();
            if (strArtista.Length == 0) strArtista = (f.strArtistaAlbum ?? "").Trim();
            if (strTitulo.Length > 0) strTitulo = Regex.Replace(CasadorDiscografia.SemNumero(strTitulo), @"^\d{1,3}[-.]\s*", "").Trim();
            if (s_rxGenerico.IsMatch(strTitulo)) strTitulo = "";

            // "aspecto-sutil": nome sem espaço nenhum é um título com hífens, não "artista-título".
            if (!s.Contains(" ") && s.Contains("-")) s = s.Replace("-", " ");

            string strEsq = null, strDir = s;
            Match m = Regex.Match(s, @"^(.+?) - (.+)$");
            if (!m.Success) m = Regex.Match(s, @"^(.+?)-(.+)$");   // "IVETE SANGALO-QUANDO A CHUVA PASSAR"
            // o hífen dentro de parêntese é do título: "Romeo und Julia (Phantasie-Ouvert"
            if (m.Success && m.Groups[1].Value.Count(c => c == '(') == m.Groups[1].Value.Count(c => c == ')'))
            { strEsq = m.Groups[1].Value.Trim(); strDir = m.Groups[2].Value.Trim(); }

            // "Artista-Título": a esquerda é artista se não for obra, número nem "Various Artists".
            bool blnVarios = strEsq != null && Regex.IsMatch(strEsq, @"^(various artists|vários artistas|varios artistas|v\.?a\.?)$", RegexOptions.IgnoreCase);
            bool blnEsqArtista = strEsq != null && !blnVarios && !Regex.IsMatch(strEsq, @"^\d") && !s_rxObra.IsMatch(strEsq)
                                 && !Regex.IsMatch(strEsq, @"antigas|track|musicas|músicas|aula", RegexOptions.IgnoreCase) && strEsq.Length <= 40;
            if (strTitulo.Length == 0)
            {
                strTitulo = blnEsqArtista || blnVarios ? strDir : s;
                if (Regex.IsMatch(strTitulo, @"^[\d\s.\-]*$")) strTitulo = "";   // "Beethoven Violin Sonatas (CD3) - 05." não tem título
                if (s_rxGenerico.IsMatch(strTitulo)) strTitulo = "";
            }
            if (strArtista.Length == 0 && blnEsqArtista) strArtista = strEsq;
            if (Regex.IsMatch(strArtista, @"^(various artists|vários artistas|unknown artist|artista desconhecido)$", RegexOptions.IgnoreCase)) strArtista = "";
            // CD ripado pelo WMP sem informação: MP3\George Michael(grupo)\Álbum Desconhecido (1_3_2006 15_53_37) — a pasta de cima é o artista.
            if (strArtista.Length == 0)
            {
                string strPai = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f.strCaminho) ?? "") ?? "") ?? "";
                strPai = Regex.Replace(strPai, @"\((grupo|group|banda)\)", "", RegexOptions.IgnoreCase).Trim();
                if (strPai.Length > 0 && !Regex.IsMatch(strPai, @"^(unknown artist|artista desconhecido|various artists|vários artistas|mp3|wma|músicas|music)$", RegexOptions.IgnoreCase))
                    strArtista = strPai;
            }

            l.strTituloBusca = strTitulo;
            l.strArtistaBusca = strArtista;

            // Indício: o que junta faixas do mesmo disco antes da busca.
            string strIndicio = "";
            if (!string.IsNullOrEmpty(f.strAlbum) && !Regex.IsMatch(f.strAlbum, @"^(unknown album|álbum desconhecido)$", RegexOptions.IgnoreCase)) strIndicio = "álbum: " + f.strAlbum.Trim();
            else if (strEsq != null && !blnEsqArtista && strEsq.Length >= 5) strIndicio = "série: " + Regex.Replace(strEsq, @"\s*\(cd\s?\d+\)", "", RegexOptions.IgnoreCase).Trim();
            else if (strArtista.Length > 0) strIndicio = "artista: " + strArtista;
            l.strIndicio = strIndicio;
        }

        /// <summary>Título truncado ("The Magic F") conta como o começo do título inteiro.</summary>
        public static double SimilaridadeTitulo(string strArquivo, string strApple)
        {
            double d = CasadorDiscografia.Similaridade(strArquivo, strApple);
            string a = DiscogsClient.NormalizarParaComparacao(strArquivo), b = DiscogsClient.NormalizarParaComparacao(strApple);
            if (a.Length >= 8 && b.StartsWith(a)) d = Math.Max(d, 0.85);
            // Clássico: "The Magic Flute, K. 620: Act 1: ..." — o arquivo costuma ter só um pedaço.
            if (a.Length >= 10 && b.Contains(a)) d = Math.Max(d, 0.8);
            return d;
        }

        /// <summary>As possibilidades da busca para a faixa, da melhor para a pior (no máximo 8, uma por disco).</summary>
        public static List<TOpcaoApple> Opcoes(TFaixaSolta l, TBuscaFaixaDto objBusca)
        {
            List<TOpcaoApple> lst = new List<TOpcaoApple>();
            if (objBusca == null || objBusca.lstFaixas == null) return lst;
            int intDur = (int)Math.Round(l.objFaixa.dblDuracao);
            foreach (TFaixaAppleDto a in objBusca.lstFaixas)
            {
                TOpcaoApple o = new TOpcaoApple { objApple = a };
                o.dblSimTitulo = SimilaridadeTitulo(l.strTituloBusca, a.strTitulo);
                double p = 1.5 * o.dblSimTitulo;
                if (intDur > 0 && a.intDuracao > 0)
                {
                    o.intDif = a.intDuracao - intDur;
                    int d = Math.Abs(o.intDif);
                    p += d <= 2 ? 1.0 : d <= 5 ? 0.6 : d <= 10 ? 0.2 : -0.6;
                }
                if (!string.IsNullOrEmpty(l.strArtistaBusca))
                {
                    o.dblSimArtista = Math.Max(CasadorDiscografia.Similaridade(l.strArtistaBusca, a.strArtista),
                                               CasadorDiscografia.Similaridade(l.strArtistaBusca, a.strArtistaAlbum));
                    p += 0.8 * o.dblSimArtista - (o.dblSimArtista < 0.4 ? 0.5 : 0);
                }
                // o álbum da tag (mesmo quebrada) confere: forte indício do disco certo
                if (!string.IsNullOrEmpty(l.objFaixa.strAlbum) && CasadorDiscografia.Similaridade(l.objFaixa.strAlbum, a.strAlbum) >= 0.7) p += 0.6;
                if (s_rxVersao.IsMatch(a.strAlbum + " " + a.strTitulo) && !s_rxVersao.IsMatch(l.strTituloBusca ?? "")) p -= 0.35;
                if (Regex.IsMatch(a.strAlbum, @"greatest hits|best of|the very best|coletânea|grandes sucessos|\bhits\b|essential|gold\b", RegexOptions.IgnoreCase)) p -= 0.15;
                o.dblPontos = p;
                lst.Add(o);
            }
            return lst.Where(o => o.dblSimTitulo >= 0.45)
                      .OrderByDescending(o => o.dblPontos)
                      .GroupBy(o => o.objApple.lngAlbumId).Select(g => g.First())
                      .Take(8).ToList();
        }

        /// <summary>alta / média / baixa, ou "" sem proposta.</summary>
        public static string Confianca(TFaixaSolta l, TOpcaoApple o)
        {
            if (o == null) return "";
            int d = o.intDif == int.MinValue ? 99 : Math.Abs(o.intDif);
            bool blnArtistaConfere = o.dblSimArtista >= 0.7;
            bool blnSemArtista = o.dblSimArtista < 0;
            // Sem artista para comparar, título comum ("Favela", "Amiga") pode ser de outro: só é alta se tudo bate em cheio.
            if (o.dblSimTitulo >= 0.85 && d <= 3 && (blnArtistaConfere || (blnSemArtista && d <= 1 && o.dblSimTitulo >= 0.95))) return "alta";
            if (o.dblSimTitulo >= 0.6 && d <= 6 && (blnArtistaConfere || blnSemArtista)) return "média";
            return "baixa";
        }

        /// <summary>
        /// A escolha final, com todas as buscas feitas: cada faixa fica com a
        /// melhor possibilidade, mais um bônus pelo disco que aparece também
        /// para outras faixas (até 3) — é o que leva as 10 do Brubeck para o
        /// mesmo álbum, em vez de cada uma para uma coletânea diferente.
        /// </summary>
        public static void Consolidar(IList<TFaixaSolta> lst, bool blnSoAlta)
        {
            Dictionary<long, int> dicDisco = new Dictionary<long, int>();
            foreach (TFaixaSolta l in lst)
                foreach (TOpcaoApple o in l.lstOpcoes.OfType<TOpcaoApple>().Where(o => o.dblSimTitulo >= 0.6))
                {
                    int n; dicDisco.TryGetValue(o.objApple.lngAlbumId, out n); dicDisco[o.objApple.lngAlbumId] = n + 1;
                }
            foreach (TFaixaSolta l in lst)
            {
                if (!l.blnBuscada) continue;
                TOpcaoApple objMelhor = null; double dblMelhor = double.MinValue;
                foreach (TOpcaoApple o in l.lstOpcoes.OfType<TOpcaoApple>())
                {
                    int n; dicDisco.TryGetValue(o.objApple.lngAlbumId, out n);
                    double p = o.dblPontos + 0.25 * Math.Min(3, Math.Max(0, n - 1));
                    if (p > dblMelhor) { dblMelhor = p; objMelhor = o; }
                }
                // Abaixo disto não há proposta: título e duração não conferem.
                if (objMelhor != null && (objMelhor.dblPontos < 1.4 || objMelhor.dblSimTitulo < 0.6)) objMelhor = null;
                l.objEscolhida = (object)objMelhor ?? TFaixaSolta.NAO_MEXER;
                if (blnSoAlta && l.strConfianca != "alta") l.blnAplicar = false;
            }
        }
    }
}
