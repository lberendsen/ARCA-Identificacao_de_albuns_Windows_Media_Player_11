using System;
using System.Collections.Generic;
using System.IO;
using ARCA.Interop;

namespace ARCA.Modelo
{
    /// <summary>
    /// A ordem das faixas de um álbum do acervo vem da tag, não do nome do
    /// arquivo. O nome é da pessoa e pode ser qualquer um; o número da faixa
    /// (WM/TrackNumber) é o que diz a posição no disco.
    ///
    /// Antes o Conferir álbum ordenava pelo nome, e isso foi usado para travar
    /// o "01 - " na renomeação (04/10/2026) — o defeito era a ordenação.
    /// </summary>
    public static class TOrdemFaixas
    {
        /// <summary>O número da faixa nos atributos, ou -1 se não houver.</summary>
        public static int NumeroDaFaixa(List<AtributoAsf> lstAtributos)
        {
            int intAntigo = -1;
            if (lstAtributos == null) return -1;
            foreach (AtributoAsf a in lstAtributos)
            {
                if (string.Equals(a.Nome, "WM/TrackNumber", StringComparison.OrdinalIgnoreCase))
                {
                    int intN = Numero(a.Valor);
                    if (intN > 0) return intN;
                }
                // Formato antigo, base zero.
                else if (string.Equals(a.Nome, "WM/Track", StringComparison.OrdinalIgnoreCase))
                {
                    int intN = Numero(a.Valor);
                    if (intN >= 0) intAntigo = intN + 1;
                }
            }
            return intAntigo;
        }

        /// <summary>O número da faixa lido do arquivo, ou -1.</summary>
        public static int NumeroDaFaixa(string strArquivo)
        {
            using (EditorAsf objEditor = new EditorAsf())
                return objEditor.Abrir(strArquivo) ? NumeroDaFaixa(objEditor.LerTudo()) : -1;
        }

        /// <summary>
        /// Os arquivos na ordem do disco: pelo número da faixa; os sem número
        /// vão para o fim, pelo nome. dicNumeros evita reabrir arquivos já
        /// lidos (pode ser null).
        /// </summary>
        public static List<string> Ordenar(IEnumerable<string> arquivos, Dictionary<string, int> dicNumeros)
        {
            List<string> lstArquivos = new List<string>(arquivos);
            Dictionary<string, int> dicN = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in lstArquivos)
            {
                int intN;
                if (dicNumeros == null || !dicNumeros.TryGetValue(f, out intN)) intN = NumeroDaFaixa(f);
                dicN[f] = intN;
            }
            lstArquivos.Sort(delegate(string a, string b)
            {
                int intA = dicN[a] > 0 ? dicN[a] : int.MaxValue;
                int intB = dicN[b] > 0 ? dicN[b] : int.MaxValue;
                int intC = intA.CompareTo(intB);
                return intC != 0 ? intC : StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
            });
            return lstArquivos;
        }

        /// <summary>
        /// Os números se repetem ou faltam? Então a ordem não é confiável — quem
        /// confere bit-perfect precisa dizer isso em vez de comparar às cegas.
        /// </summary>
        public static bool NumeracaoCompleta(IEnumerable<string> arquivos, Dictionary<string, int> dicNumeros)
        {
            HashSet<int> setVistos = new HashSet<int>();
            foreach (string f in arquivos)
            {
                int intN;
                if (!dicNumeros.TryGetValue(f, out intN) || intN <= 0 || !setVistos.Add(intN)) return false;
            }
            return true;
        }

        private static int Numero(string strValor)
        {
            // "3", "03", "3/12"
            if (string.IsNullOrEmpty(strValor)) return -1;
            int intFim = 0;
            string s = strValor.Trim();
            while (intFim < s.Length && char.IsDigit(s[intFim])) intFim++;
            int intN;
            return intFim > 0 && int.TryParse(s.Substring(0, intFim), out intN) ? intN : -1;
        }
    }
}
