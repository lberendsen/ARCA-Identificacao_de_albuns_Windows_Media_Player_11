using System;
using System.Collections.Generic;

namespace ARCA.Modelo
{
    /// <summary>
    /// Metadados e geometria física de uma faixa de CD para extração e identificação.
    /// Utiliza notação húngara e tipos estritos.
    /// </summary>
    public class TrackInfo
    {
        public int TrackNum;
        public long ByteOffset;
        public long EndByteOffset;
        public long StartLba;
        public int SessionNumber = 1;
        public string Title;
        public string Artist;
        public string Album;
        public string ISRC;
        public string Composer;
        public bool blnIdentificadoMusicBrainz;
        public string TrackType = "AUDIO";
        public string WmaFilePath;
        public string ExtractedFilePath;
        public string CoverUrl;
        public uint Year;
        public string FullDate;
        public string Genre;

        public bool WmaCreated;
        public bool MetadataWritten;
        public bool MetadataValidated;
        public bool CodecValidated;
        public string FailureReason;

        public List<string> PossibleAlbums = new List<string>();
        public List<string> PossibleCoverUrls = new List<string>();
        public List<string> PossibleFullDates = new List<string>();
        public Dictionary<string, int> GenreVotes = new Dictionary<string, int>();
    }
}
