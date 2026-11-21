using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ARCA.Central;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>
    /// A gravação da separação do Álbum Desconhecido, disco a disco (sem tela,
    /// para a sonda também usar). Para cada disco do Apple Music que recebeu
    /// faixas: o álbum inteiro pelo link (títulos com acento, capa, gênero,
    /// ano), a posição de cada faixa nele, e a gravação pelo GravadorMp3 —
    /// cópia, conferência do áudio, troca —, movendo para
    /// MP3\&lt;Artista&gt;\&lt;Ano&gt; - &lt;Álbum&gt;\NN Título.mp3.
    /// </summary>
    public static class SeparadorDesconhecido
    {
        /// <summary>A pasta MP3 do acervo, a partir de uma faixa do Unknown Artist\Unknown Album (dois níveis acima).</summary>
        public static string RaizDoAcervo(string strFaixa)
        {
            string strAlbum = Path.GetDirectoryName(strFaixa);
            string strArtista = Path.GetDirectoryName(strAlbum);
            return Path.GetDirectoryName(strArtista) ?? strAlbum;
        }

        public static string PastaDoDisco(string strRaiz, string strArtista, string strAno, string strAlbum)
        {
            return Path.Combine(Path.Combine(strRaiz, OrganizadorDiscografia.NomeDePasta(strArtista)),
                                OrganizadorDiscografia.NomeDePasta((strAno ?? "").Length == 4 ? strAno + " - " + strAlbum : strAlbum));
        }

        /// <summary>Grava as faixas de um disco. Devolve quantas foram gravadas.</summary>
        public static int GravarDisco(List<TFaixaSolta> lst, string strRaiz, bool blnCapas, bool blnMover, Action<string> actLog)
        {
            TFaixaAppleDto a0 = lst[0].objOpcao.objApple;
            actLog(string.Format("== {0} — {1} ({2} faixa(s)) — {3}", a0.strArtistaAlbum, a0.strAlbum, lst.Count, a0.strLinkAlbum));
            // Pelo ID do álbum: o link que a busca devolve é o da música ("...?i=…") e trazia outro lançamento.
            TResposta r = ClienteCentral.PorLink(a0.lngAlbumId.ToString(), a0.intTotalFaixas);
            if (r.objCandidato == null) throw new Exception("o álbum não veio do Apple Music (" + r.strErro + ")");
            TCandidatoIdentificacao c = r.objCandidato.ParaCandidato();
            if (c.lstTitulos.Count == 0) throw new Exception("o álbum veio sem faixas");

            List<TFaixaMp3> lstF = new List<TFaixaMp3>();
            foreach (TFaixaSolta l in lst)
            {
                TFaixaAppleDto a = l.objOpcao.objApple;
                int j = -1;
                // disco 1 (ou único): a posição é o número da faixa, se o título confere
                if (a.intDisco <= 1 && a.intFaixa >= 1 && a.intFaixa <= c.lstTitulos.Count
                    && CasadorFaixasSoltas.SimilaridadeTitulo(a.strTitulo, c.lstTitulos[a.intFaixa - 1]) >= 0.8) j = a.intFaixa - 1;
                if (j < 0)
                {
                    double dblMelhor = -1;
                    for (int k = 0; k < c.lstTitulos.Count; k++)
                    {
                        double s = CasadorFaixasSoltas.SimilaridadeTitulo(a.strTitulo, c.lstTitulos[k]);
                        int intDurK = k < c.lstDuracoes.Count ? c.lstDuracoes[k] : -1;
                        if (intDurK > 0 && a.intDuracao > 0) s += Math.Abs(intDurK - a.intDuracao) <= 2 ? 0.2 : 0;
                        if (s > dblMelhor) { dblMelhor = s; j = k; }
                    }
                    if (dblMelhor < 0.6) { l.strResultado = "não achei a faixa no álbum"; actLog("   " + l.strArquivo + ": não achei no álbum; fica como está"); continue; }
                }
                TFaixaMp3 f = l.objFaixa;
                f.intPosicaoNova = j;
                f.strTituloNovo = c.lstTitulos[j];
                string strArtFaixa = j < c.lstArtistasFaixas.Count ? c.lstArtistasFaixas[j] : "";
                f.strArtistaNovo = string.Equals((strArtFaixa ?? "").Trim(), (c.strArtista ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? "" : strArtFaixa;
                f.strCompositorNovo = c.lstCompositores != null && j < c.lstCompositores.Count ? c.lstCompositores[j] : "";
                f.blnAplicar = true;
                lstF.Add(f);
            }
            if (lstF.Count == 0) return 0;

            // duas faixas do arquivo no mesmo lugar do disco: só a primeira (a outra é outra gravação)
            foreach (var g in lstF.GroupBy(f => f.intPosicaoNova).Where(g => g.Count() > 1))
                foreach (TFaixaMp3 f in g.Skip(1))
                {
                    f.blnAplicar = false;
                    f.strResultado = "mesma posição de outra faixa no disco; não gravada";
                    actLog("   " + f.strArquivo + ": cai na mesma faixa " + (f.intPosicaoNova + 1) + " de outra; fica como está");
                }

            string strAno = c.strAno != null && c.strAno.Length >= 4 ? c.strAno.Substring(0, 4) : "";
            TDadosAlbum objDados = new TDadosAlbum
            {
                strArtistaAlbum = c.strArtista,
                strAlbum = c.strAlbum,
                strAno = strAno,
                strGenero = ((c.strGenero ?? "").Split(',').Select(s => s.Trim()).FirstOrDefault() ?? ""),
                intTotalFaixas = c.lstTitulos.Count,
                strFonte = "Apple Music" + (string.IsNullOrEmpty(c.strDetalhe) ? "" : " · " + c.strDetalhe),
                arrCapa = blnCapas ? c.arrCapa : null,
                strOrigemCapa = "Apple Music",
                strEvento = "separação do Álbum Desconhecido (ARCA Identificar)",
                blnRenomearAoMover = true
            };
            string strDestino = blnMover ? PastaDoDisco(strRaiz, c.strArtista, strAno, c.strAlbum) : null;
            int intOk = GravadorMp3.Aplicar(lstF, objDados, actLog, delegate { }, strDestino, blnMover);
            foreach (TFaixaSolta l in lst) if (lstF.Contains(l.objFaixa)) l.strResultado = l.objFaixa.strResultado;
            return intOk;
        }
    }
}
