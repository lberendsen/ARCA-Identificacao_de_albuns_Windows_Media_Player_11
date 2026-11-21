using System;
using System.Collections.Generic;

namespace ARCA.Modelo
{
    /// <summary>
    /// Um resultado de identificação, na mesma forma venha de onde vier.
    ///
    /// Existe para que a escolha entre Apple Music e Discogs seja feita pela
    /// pessoa, lado a lado, e para que aplicar o escolhido seja um caminho só
    /// — sem um ramo para cada serviço no momento de gravar as tags.
    /// </summary>
    public sealed class TCandidatoIdentificacao
    {
        public string strFonte;                 // "Apple Music" ou "Discogs"
        public string strArtista;
        public string strAlbum;
        public string strAno;
        public string strGenero;
        /// <summary>Gravadora/catálogo/país, quando a fonte informa.</summary>
        public string strDetalhe;

        public List<string> lstTitulos = new List<string>();
        public List<string> lstArtistasFaixas = new List<string>();
        /// <summary>null quando a fonte não traz compositores.</summary>
        public List<string> lstCompositores;
        /// <summary>Segundos por faixa; -1 quando a fonte não informa.</summary>
        public List<int> lstDuracoes = new List<int>();

        public byte[] arrCapa;
        /// <summary>
        /// A capa sem redução (scan cortado), para a pasta Artwork. null quando
        /// a capa só existe no tamanho de arrCapa (internet) — aí vai arrCapa.
        /// </summary>
        public byte[] arrCapaPlena;
        /// <summary>Páginas do encarte digitalizadas, em ordem, já em JPEG. null quando não há.</summary>
        public List<byte[]> lstEncarte;

        public static TCandidatoIdentificacao DeItunes(TItunesAlbumInfo objAlbum, byte[] arrCapa)
        {
            if (objAlbum == null) return null;
            TCandidatoIdentificacao objC = new TCandidatoIdentificacao();
            objC.strFonte = "Apple Music";
            objC.strArtista = objAlbum.strArtistName;
            objC.strAlbum = objAlbum.strCollectionName;
            objC.strAno = objAlbum.strReleaseYear;
            objC.strGenero = objAlbum.strPrimaryGenreName;
            objC.strDetalhe = string.IsNullOrEmpty(objAlbum.strUpc) ? null : "UPC " + objAlbum.strUpc;
            if (objAlbum.lstFaixas != null)
            {
                foreach (TItunesTrackInfo objF in objAlbum.lstFaixas)
                {
                    objC.lstTitulos.Add(objF.strTrackName);
                    objC.lstArtistasFaixas.Add(objF.strArtistName);
                    objC.lstDuracoes.Add(objF.intTrackTimeMillis > 0
                        ? (int)Math.Round(objF.intTrackTimeMillis / 1000.0) : -1);
                }
            }
            objC.arrCapa = arrCapa;
            return objC;
        }

        public static TCandidatoIdentificacao DeDiscogs(TDiscogsReleaseInfo objRelease)
        {
            if (objRelease == null) return null;
            TCandidatoIdentificacao objC = new TCandidatoIdentificacao();
            objC.strFonte = "Discogs";
            objC.strArtista = objRelease.strArtist;
            objC.strAlbum = objRelease.strAlbum;
            objC.strAno = objRelease.strYear;
            List<string> lstGeneros = new List<string>(objRelease.lstGenres);
            lstGeneros.AddRange(objRelease.lstStyles);
            objC.strGenero = lstGeneros.Count > 0 ? string.Join(", ", lstGeneros.ToArray()) : null;

            List<string> lstDetalhe = new List<string>();
            if (!string.IsNullOrEmpty(objRelease.strGravadora)) lstDetalhe.Add(objRelease.strGravadora);
            if (!string.IsNullOrEmpty(objRelease.strPais)) lstDetalhe.Add(objRelease.strPais);
            if (objRelease.intReleaseId > 0) lstDetalhe.Add("release " + objRelease.intReleaseId);
            objC.strDetalhe = lstDetalhe.Count > 0 ? string.Join(" · ", lstDetalhe.ToArray()) : null;

            objC.lstTitulos.AddRange(objRelease.lstTrackTitles);
            objC.lstArtistasFaixas.AddRange(objRelease.lstTrackArtists);
            objC.lstDuracoes.AddRange(objRelease.lstTrackDurations);
            if (objRelease.lstTrackComposers.Exists(c => !string.IsNullOrEmpty(c)))
                objC.lstCompositores = new List<string>(objRelease.lstTrackComposers);
            objC.arrCapa = objRelease.arrCoverBytes;
            return objC;
        }
    }
}
