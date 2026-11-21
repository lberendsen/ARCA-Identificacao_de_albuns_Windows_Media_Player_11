// O que atravessa o cabo entre o ARCA Central (notebook) e o ARCA Identificar (Vista).
// Compilado pelos dois lados: um só desenho, impossível de divergir.
// JSON pelo JavaScriptSerializer (System.Web.Extensions), presente nos dois .NET.

using System;
using System.Collections.Generic;
using ARCA.Modelo;

namespace ARCA.Central
{
    /// <summary>O pedido do Vista. Campos que não se aplicam vêm vazios.</summary>
    public sealed class TPedido
    {
        public string strArtista;
        public string strAlbum;
        public string strUrl;
        public int intFaixas;
        /// <summary>/digitalizar: área a partir do canto de referência da mesa, e resolução.</summary>
        public double dblLarguraCm;
        public double dblAlturaCm;
        public int intDpi;
        /// <summary>/faixa: o título provável (e strArtista, se souber).</summary>
        public string strTitulo;
        /// <summary>/toc: o índice do CD de origem que o WMP grava nas faixas (WM/MCDI, "D+96+3F51+…").</summary>
        public string strMcdi;
        /// <summary>/som: os caminhos das faixas como o Vista as vê (D:\…); o Central as lê pelo compartilhamento.</summary>
        public List<string> lstCaminhos;
        /// <summary>/accuraterip: quantas faixas do índice são áudio (CD Extra tem uma de dados a mais); 0 = todas.</summary>
        public int intFaixasAudio;
    }

    /// <summary>Uma música do Apple Music achada por /faixa: o suficiente para casar e para pedir o álbum depois por /link.</summary>
    public sealed class TFaixaAppleDto
    {
        public string strArtista;
        public string strAlbum;
        public string strArtistaAlbum;
        public string strTitulo;
        public int intFaixa;
        public int intTotalFaixas;
        public int intDisco;
        public int intTotalDiscos;
        public int intAno;
        public int intDuracao;
        public string strGenero;
        public long lngAlbumId;
        public string strLinkAlbum;
    }

    /// <summary>Resposta de /faixa: as músicas que a busca trouxe (o casamento é feito no Vista).</summary>
    public sealed class TBuscaFaixaDto
    {
        public string strTermo;
        public bool blnDoCache;
        public List<TFaixaAppleDto> lstFaixas = new List<TFaixaAppleDto>();
        public string strErro;
    }

    /// <summary>TCandidatoIdentificacao na forma que atravessa o cabo (capa em base64).</summary>
    public sealed class TCandidatoDto
    {
        public string strFonte;
        public string strArtista;
        public string strAlbum;
        public string strAno;
        public string strGenero;
        public string strDetalhe;
        public List<string> lstTitulos;
        public List<string> lstArtistasFaixas;
        public List<string> lstCompositores;
        public List<int> lstDuracoes;
        public string strCapaBase64;

        public static TCandidatoDto De(TCandidatoIdentificacao c)
        {
            if (c == null) return null;
            TCandidatoDto d = new TCandidatoDto();
            d.strFonte = c.strFonte;
            d.strArtista = c.strArtista;
            d.strAlbum = c.strAlbum;
            d.strAno = c.strAno;
            d.strGenero = c.strGenero;
            d.strDetalhe = c.strDetalhe;
            d.lstTitulos = c.lstTitulos;
            d.lstArtistasFaixas = c.lstArtistasFaixas;
            d.lstCompositores = c.lstCompositores;
            d.lstDuracoes = c.lstDuracoes;
            d.strCapaBase64 = c.arrCapa != null && c.arrCapa.Length > 0 ? Convert.ToBase64String(c.arrCapa) : null;
            return d;
        }

        public TCandidatoIdentificacao ParaCandidato()
        {
            TCandidatoIdentificacao c = new TCandidatoIdentificacao();
            c.strFonte = strFonte;
            c.strArtista = strArtista;
            c.strAlbum = strAlbum;
            c.strAno = strAno;
            c.strGenero = strGenero;
            c.strDetalhe = strDetalhe;
            c.lstTitulos = lstTitulos ?? new List<string>();
            c.lstArtistasFaixas = lstArtistasFaixas ?? new List<string>();
            c.lstCompositores = lstCompositores;
            c.lstDuracoes = lstDuracoes ?? new List<int>();
            c.arrCapa = string.IsNullOrEmpty(strCapaBase64) ? null : Convert.FromBase64String(strCapaBase64);
            return c;
        }
    }

    /// <summary>Um disco da discografia (master do Discogs): só o que o casamento precisa. A capa vem depois, por /link.</summary>
    public sealed class TDiscoDto
    {
        public int intMasterId;
        public string strTitulo;
        public int intAno;
        public List<string> lstTitulos = new List<string>();
        public List<int> lstDuracoes = new List<int>();
        public string Link() { return "https://www.discogs.com/master/" + intMasterId; }
    }

    /// <summary>
    /// Resposta de /discografia. A primeira leitura de um artista grande leva
    /// minutos (o Discogs atende 60 pedidos por minuto): enquanto não está
    /// pronta, vem só o andamento, e o Vista pergunta de novo.
    /// </summary>
    public sealed class TDiscografiaDto
    {
        public string strArtista;
        public int intArtistaId;
        public bool blnPronta;
        public int intLidos;
        public int intTotal;
        public string strFase;
        public List<TDiscoDto> lstDiscos = new List<TDiscoDto>();
        public string strErro;
    }

    public sealed class TResposta
    {
        public TCandidatoDto objApple;
        public TCandidatoDto objDiscogs;
        /// <summary>Resposta de /link: o lançamento exato.</summary>
        public TCandidatoDto objCandidato;
        /// <summary>Resposta de /toc: os discos achados pelo índice (MusicBrainz, GnuDB), o mais provável primeiro.</summary>
        public List<TCandidatoDto> lstCandidatos = new List<TCandidatoDto>();
        /// <summary>Resposta de /accuraterip: o arquivo dBAR-….bin da base, em base64; vazio com blnForaDaBase = o disco não está lá.</summary>
        public string strDadosBase64;
        public bool blnForaDaBase;
        public string strUrl;
        public string strErro;
        public List<string> lstLog = new List<string>();
    }
}
