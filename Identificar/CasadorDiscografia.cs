using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ARCA.Central;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>O que se sabe de uma faixa do acervo, para casar com a discografia.</summary>
    public sealed class TFaixaEntrada
    {
        public string strTitulo;
        /// <summary>Ano que a faixa sugere (tag, álbum "1966", pasta "1966"); 0 = não se sabe.</summary>
        public int intAno;
        public int intDuracao;
        /// <summary>De onde veio (pasta + álbum atual): faixas do mesmo grupo tendem a ser do mesmo disco.</summary>
        public string strGrupo;
    }

    /// <summary>Uma possibilidade: a faixa é a posição intPosicao do disco.</summary>
    public sealed class TOpcaoDisco
    {
        public TDiscoDto objDisco;
        public int intPosicao;
        public double dblSimilaridade;
        public double dblPontos;
        public int intDifDuracao = int.MinValue;

        public string strTituloNoDisco { get { return objDisco.lstTitulos[intPosicao]; } }

        public override string ToString()
        {
            return string.Format("{0} · {1} ({2} f.) — {3}. {4}", objDisco.intAno, objDisco.strTitulo, objDisco.lstTitulos.Count, intPosicao + 1, strTituloNoDisco);
        }
    }

    /// <summary>
    /// Descobre o disco de cada faixa pela discografia do artista: título da
    /// faixa (sem acento, com tolerância a grafia), ano e duração. Faixas que
    /// vieram juntas (mesma pasta, mesmo "álbum") puxam para o mesmo disco —
    /// é assim que um LP com 12 faixas ganha da coletânea que repete 5 delas.
    /// </summary>
    public static class CasadorDiscografia
    {
        public const double SIMILARIDADE_MINIMA = 0.78;
        private const int MAX_OPCOES = 8;

        private static readonly HashSet<string> s_setVazias = new HashSet<string>
        { "o", "a", "os", "as", "um", "uma", "de", "do", "da", "dos", "das", "e", "eh", "em", "no", "na", "que", "the", "el", "la", "los", "y" };

        /// <summary>O ano que a faixa sugere: a tag; senão o álbum ou a pasta que são só um ano.</summary>
        public static int AnoSugerido(uint uintAnoTag, string strAlbum, string strPasta)
        {
            if (uintAnoTag >= 1900 && uintAnoTag <= 2100) return (int)uintAnoTag;
            Match m = Regex.Match((strAlbum ?? "").Trim(), @"^((19|20)\d\d)\b");
            if (m.Success) return int.Parse(m.Groups[1].Value);
            m = Regex.Match((strPasta ?? "").Trim(), @"^((19|20)\d\d)\b");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        public static double Similaridade(string a, string b)
        {
            return SimilaridadeNormalizada(DiscogsClient.NormalizarParaComparacao(a), DiscogsClient.NormalizarParaComparacao(b));
        }

        private static double SimilaridadeNormalizada(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            int intMin = Math.Min(a.Length, b.Length), intMax = Math.Max(a.Length, b.Length);
            double dblMelhor = 0;
            if ((a.Contains(b) || b.Contains(a)) && (double)intMin / intMax >= 0.6) dblMelhor = 0.86;
            if ((double)intMin / intMax >= 0.6)
                dblMelhor = Math.Max(dblMelhor, 1.0 - (double)Levenshtein(a, b) / intMax);
            HashSet<string> sa = Palavras(a), sb = Palavras(b);
            if (sa.Count > 0 && sb.Count > 0)
            {
                int intComuns = sa.Count(sb.Contains);
                double dblRecall = (double)intComuns / Math.Min(sa.Count, sb.Count);
                double dblJaccard = (double)intComuns / (sa.Count + sb.Count - intComuns);
                if (dblRecall >= 0.99 && dblJaccard >= 0.5) dblMelhor = Math.Max(dblMelhor, 0.8 + 0.15 * dblJaccard);
            }
            return dblMelhor;
        }

        private static HashSet<string> Palavras(string s)
        {
            HashSet<string> h = new HashSet<string>(s.Split(' ').Where(p => p.Length > 0 && !s_setVazias.Contains(p)));
            return h.Count > 0 ? h : new HashSet<string>(s.Split(' ').Where(p => p.Length > 0));
        }

        private static int Levenshtein(string a, string b)
        {
            int[] ant = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) ant[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, ant[j] + 1), ant[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                int[] t = ant; ant = cur; cur = t;
            }
            return ant[b.Length];
        }

        /// <summary>Para cada faixa, as melhores possibilidades (no máximo uma por disco), da mais provável para a menos.</summary>
        /// <summary>
        /// As formas de um título do Discogs: inteiro e cada lado do " = "
        /// ("Esqueça = Forget Him" vale como "Esqueça" e como "Forget Him").
        /// </summary>
        private static string[] Formas(string strTitulo)
        {
            List<string> l = new List<string> { DiscogsClient.NormalizarParaComparacao(strTitulo) };
            if ((strTitulo ?? "").Contains("="))
                foreach (string p in strTitulo.Split('='))
                {
                    string n = DiscogsClient.NormalizarParaComparacao(p);
                    if (n.Length > 0 && !l.Contains(n)) l.Add(n);
                }
            return l.ToArray();
        }

        /// <summary>O título do acervo sem o número da faixa na frente ("01 Pra Sempre").</summary>
        public static string SemNumero(string s)
        {
            string t = Regex.Replace(s ?? "", @"^\s*\d{1,3}\s*[-–.)_]?\s+", "");
            return t.Trim().Length > 0 ? t : (s ?? "");
        }

        public static List<List<TOpcaoDisco>> Opcoes(List<TFaixaEntrada> lstFaixas, List<TDiscoDto> lstDiscos)
        {
            // normaliza uma vez só: são milhares de títulos
            List<string[][]> lstNorm = lstDiscos.Select(d => d.lstTitulos.Select(t => Formas(t)).ToArray()).ToList();
            List<List<TOpcaoDisco>> lstRes = new List<List<TOpcaoDisco>>();
            foreach (TFaixaEntrada f in lstFaixas)
            {
                string strN = DiscogsClient.NormalizarParaComparacao(SemNumero(f.strTitulo));
                List<TOpcaoDisco> lst = new List<TOpcaoDisco>();
                for (int d = 0; d < lstDiscos.Count; d++)
                {
                    TOpcaoDisco melhor = null;
                    for (int j = 0; j < lstNorm[d].Length; j++)
                    {
                        double s = 0;
                        foreach (string strForma in lstNorm[d][j]) s = Math.Max(s, SimilaridadeNormalizada(strN, strForma));
                        if (s < SIMILARIDADE_MINIMA) continue;
                        TOpcaoDisco o = new TOpcaoDisco { objDisco = lstDiscos[d], intPosicao = j, dblSimilaridade = s };
                        o.dblPontos = Pontuar(f, o);
                        if (melhor == null || o.dblPontos > melhor.dblPontos) melhor = o;
                    }
                    if (melhor != null) lst.Add(melhor);
                }
                lstRes.Add(lst.OrderByDescending(o => o.dblPontos).ThenBy(o => o.objDisco.intAno).Take(MAX_OPCOES).ToList());
            }
            return lstRes;
        }

        private static double Pontuar(TFaixaEntrada f, TOpcaoDisco o)
        {
            double p = o.dblSimilaridade;
            int intAnoDisco = o.objDisco.intAno;
            if (f.intAno > 0 && intAnoDisco > 0)
            {
                int dif = Math.Abs(f.intAno - intAnoDisco);
                p += dif == 0 ? 0.5 : dif == 1 ? 0.25 : -0.4;
            }
            // LP ganha de compacto e de single: um disco de 2 a 4 faixas raramente é a origem
            int n = o.objDisco.lstTitulos.Count;
            p += n >= 8 ? 0.15 : n >= 5 ? 0.05 : -0.1;
            int intDurDisco = o.intPosicao < o.objDisco.lstDuracoes.Count ? o.objDisco.lstDuracoes[o.intPosicao] : -1;
            if (f.intDuracao > 0 && intDurDisco > 0)
            {
                o.intDifDuracao = intDurDisco - f.intDuracao;
                int dif = Math.Abs(o.intDifDuracao);
                p += dif <= 3 ? 0.3 : dif <= 8 ? 0.15 : dif <= 20 ? 0 : -0.3;
            }
            return p;
        }

        /// <summary>
        /// Escolhe uma possibilidade por faixa (null = nenhuma). Cada grupo
        /// elege o disco que mais pontua somando suas faixas; a faixa vai para
        /// ele se lá estiver quase tão bem quanto no seu melhor disco. Dentro do
        /// grupo, uma posição do disco não recebe duas faixas.
        /// </summary>
        public static TOpcaoDisco[] Escolher(List<TFaixaEntrada> lstFaixas, List<List<TOpcaoDisco>> lstOpcoes)
        {
            TOpcaoDisco[] vet = new TOpcaoDisco[lstFaixas.Count];
            foreach (IGrouping<string, int> g in Enumerable.Range(0, lstFaixas.Count).GroupBy(i => lstFaixas[i].strGrupo ?? ""))
            {
                Dictionary<TDiscoDto, double> dicSoma = new Dictionary<TDiscoDto, double>();
                foreach (int i in g)
                    foreach (TOpcaoDisco o in lstOpcoes[i])
                        dicSoma[o.objDisco] = (dicSoma.ContainsKey(o.objDisco) ? dicSoma[o.objDisco] : 0) + o.dblPontos;
                TDiscoDto objDoGrupo = dicSoma.Count == 0 ? null
                    : dicSoma.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.intAno).First().Key;

                // candidatos por faixa: o disco do grupo primeiro (se for razoável), depois o resto
                List<Tuple<double, int, TOpcaoDisco>> lstPares = new List<Tuple<double, int, TOpcaoDisco>>();
                foreach (int i in g)
                {
                    if (lstOpcoes[i].Count == 0) continue;
                    double dblMelhor = lstOpcoes[i][0].dblPontos;
                    foreach (TOpcaoDisco o in lstOpcoes[i])
                    {
                        double p = o.dblPontos;
                        if (o.objDisco == objDoGrupo && p >= dblMelhor - 0.35) p += 1.0;   // coerência do grupo
                        lstPares.Add(Tuple.Create(p, i, o));
                    }
                }
                HashSet<string> setUsadas = new HashSet<string>();
                foreach (Tuple<double, int, TOpcaoDisco> t in lstPares.OrderByDescending(x => x.Item1))
                {
                    if (vet[t.Item2] != null) continue;
                    string strPos = t.Item3.objDisco.intMasterId + "/" + t.Item3.intPosicao;
                    if (setUsadas.Contains(strPos)) continue;
                    vet[t.Item2] = t.Item3;
                    setUsadas.Add(strPos);
                }
            }
            return vet;
        }

        /// <summary>"alta", "média" ou "baixa", para a pessoa saber onde olhar.</summary>
        public static string Confianca(TFaixaEntrada f, TOpcaoDisco o)
        {
            if (o == null) return "";
            bool blnAno = f.intAno > 0 && Math.Abs(f.intAno - o.objDisco.intAno) <= 1;
            bool blnDur = o.intDifDuracao != int.MinValue && Math.Abs(o.intDifDuracao) <= 8;
            if (o.dblSimilaridade >= 0.92 && (blnAno || blnDur)) return "alta";
            if (o.dblSimilaridade >= 0.85 && (blnAno || blnDur || f.intAno == 0)) return "média";
            return "baixa";
        }
    }
}
