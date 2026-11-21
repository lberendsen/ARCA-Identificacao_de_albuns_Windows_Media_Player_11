using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace ARCA.Identificar
{
    /// <summary>
    /// Uma faixa (MP3 ou WMA) como está no disco, mais o que a identificação
    /// propõe para ela. É também a linha da grade da janela.
    /// </summary>
    public sealed class TFaixaMp3 : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Avisar(string strPropriedade)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(strPropriedade));
        }

        // ---- o que está no arquivo
        public string strCaminho { get; set; }
        public string strArquivo { get { return Path.GetFileName(strCaminho); } }
        public int intFaixaTag { get; set; }
        public string strTitulo { get; set; }
        public string strArtista { get; set; }
        public string strArtistaAlbum { get; set; }
        public string strAlbum { get; set; }
        public uint uintAno { get; set; }
        public string strGenero { get; set; }
        public double dblDuracao { get; set; }
        public bool blnTemCapa { get; set; }
        public byte[] arrCapaEmbutida { get; set; }

        public string strDuracao { get { return Formatar(dblDuracao); } }
        public string strNumero { get { return intFaixaTag > 0 ? intFaixaTag.ToString() : ""; } }

        // ---- o que a identificação propõe
        private int m_intPosicaoNova = -1;
        private string m_strTituloNovo, m_strArtistaNovo, m_strCompositorNovo, m_strDif;
        private bool m_blnAplicar;
        private Brush m_objCorDif = Brushes.Transparent;

        /// <summary>Posição (0..n-1) na lista de faixas do lançamento escolhido; -1 = sem par.</summary>
        public int intPosicaoNova { get { return m_intPosicaoNova; } set { m_intPosicaoNova = value; Avisar("intPosicaoNova"); Avisar("strPosicaoNova"); } }
        public string strPosicaoNova { get { return m_intPosicaoNova >= 0 ? (m_intPosicaoNova + 1).ToString() : "—"; } }
        public string strTituloNovo { get { return m_strTituloNovo; } set { m_strTituloNovo = value; Avisar("strTituloNovo"); } }
        public string strArtistaNovo { get { return m_strArtistaNovo; } set { m_strArtistaNovo = value; Avisar("strArtistaNovo"); } }
        public string strCompositorNovo { get { return m_strCompositorNovo; } set { m_strCompositorNovo = value; Avisar("strCompositorNovo"); } }
        public string strDif { get { return m_strDif; } set { m_strDif = value; Avisar("strDif"); } }
        public Brush objCorDif { get { return m_objCorDif; } set { m_objCorDif = value; Avisar("objCorDif"); } }
        public bool blnAplicar { get { return m_blnAplicar; } set { m_blnAplicar = value; Avisar("blnAplicar"); } }

        // ---- resultado da gravação
        private string m_strResultado;
        public string strResultado { get { return m_strResultado; } set { m_strResultado = value; Avisar("strResultado"); } }

        public static string Formatar(double dblSegundos)
        {
            if (dblSegundos <= 0) return "";
            TimeSpan t = TimeSpan.FromSeconds(Math.Round(dblSegundos));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        /// <summary>Lê as tags e a duração. Não escreve nada.</summary>
        public static TFaixaMp3 Ler(string strCaminho)
        {
            TFaixaMp3 f = new TFaixaMp3 { strCaminho = strCaminho };
            using (TagLib.File t = TagLib.File.Create(strCaminho))
            {
                f.intFaixaTag = (int)t.Tag.Track;
                f.strTitulo = t.Tag.Title ?? "";
                f.strArtista = string.Join("; ", t.Tag.Performers ?? new string[0]);
                f.strArtistaAlbum = string.Join("; ", t.Tag.AlbumArtists ?? new string[0]);
                f.strAlbum = t.Tag.Album ?? "";
                f.uintAno = t.Tag.Year;
                f.strGenero = string.Join("; ", t.Tag.Genres ?? new string[0]);
                f.dblDuracao = t.Properties != null ? t.Properties.Duration.TotalSeconds : 0;
                TagLib.IPicture objFoto = (t.Tag.Pictures ?? new TagLib.IPicture[0])
                    .FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? (t.Tag.Pictures ?? new TagLib.IPicture[0]).FirstOrDefault();
                f.blnTemCapa = objFoto != null;
                if (objFoto != null) f.arrCapaEmbutida = objFoto.Data.Data;
            }
            if (f.strTitulo.Length == 0 || f.strArtista.Length == 0 || f.strAlbum.Length == 0) CompletarDaTagQuebrada(f);
            return f;
        }

        /// <summary>
        /// Tag quebrada de iTunes antigo: cabeçalho ID3v2.3 com quadros no formato
        /// v2.2 ("TT2", "TP1", "TAL", "TRK", "TYE" com 4 bytes de nome). Nem o
        /// taglib-sharp nem o WMP leem — a faixa cai no "Álbum Desconhecido" com
        /// título, artista e álbum guardados dentro (130 faixas do Unknown Album,
        /// 20/11/2026). Aqui só se LÊ, para completar o que veio vazio; ao gravar,
        /// o taglib escreve os quadros certos.
        /// </summary>
        private static void CompletarDaTagQuebrada(TFaixaMp3 f)
        {
            try
            {
                byte[] b;
                using (FileStream fs = new FileStream(f.strCaminho, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] cab = new byte[10];
                    if (fs.Read(cab, 0, 10) < 10 || cab[0] != 'I' || cab[1] != 'D' || cab[2] != '3' || cab[3] < 3) return;
                    int intTam = ((cab[6] & 0x7F) << 21) | ((cab[7] & 0x7F) << 14) | ((cab[8] & 0x7F) << 7) | (cab[9] & 0x7F);
                    b = new byte[Math.Min(intTam, 4 * 1024 * 1024)];
                    int n = 0, r;
                    while (n < b.Length && (r = fs.Read(b, n, b.Length - n)) > 0) n += r;
                }
                Dictionary<string, string> dic = new Dictionary<string, string>();
                int i = 0;
                while (i + 10 <= b.Length && b[i] != 0)
                {
                    string strId = Encoding.ASCII.GetString(b, i, 4);
                    int intTam = (b[i + 4] << 24) | (b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7];
                    if (intTam <= 0 || i + 10 + intTam > b.Length) break;
                    if ((strId[3] == ' ' || strId[3] == '\0') && strId[0] == 'T' && intTam > 1)
                        dic[strId.Substring(0, 3)] = TextoDoQuadro(b, i + 10, intTam);
                    i += 10 + intTam;
                }
                string s;
                if (f.strTitulo.Length == 0 && dic.TryGetValue("TT2", out s)) f.strTitulo = s;
                if (f.strArtista.Length == 0 && dic.TryGetValue("TP1", out s)) f.strArtista = s;
                if (f.strArtistaAlbum.Length == 0 && dic.TryGetValue("TP2", out s)) f.strArtistaAlbum = s;
                if (f.strAlbum.Length == 0 && dic.TryGetValue("TAL", out s)) f.strAlbum = s;
                if (f.intFaixaTag == 0 && dic.TryGetValue("TRK", out s)) { int n; if (int.TryParse(s.Split('/')[0].Trim(), out n)) f.intFaixaTag = n; }
                if (f.uintAno == 0 && dic.TryGetValue("TYE", out s)) { uint a; if (uint.TryParse(s.Trim(), out a)) f.uintAno = a; }
                if (f.strGenero.Length == 0 && dic.TryGetValue("TCO", out s)) f.strGenero = s;
            }
            catch { }
        }

        /// <summary>
        /// O índice do CD de origem que o WMP grava ao ripar ("D+96+3F51+…", o
        /// mesmo texto do WM/MCDI), ou null. WMA: atributo WM/MCDI. MP3: quadro
        /// ID3 "MCDI" (v2.3/2.4) ou "MCI" (v2.2), que o WMP grava como o mesmo
        /// texto em UTF-16; se vier como o índice binário do Windows (CDROM_TOC),
        /// é convertido.
        /// </summary>
        public static string IndiceDoCd(string strArquivo)
        {
            try
            {
                if (EhWma(strArquivo))
                    using (ARCA.Interop.EditorAsf e = new ARCA.Interop.EditorAsf())
                    {
                        if (!e.Abrir(strArquivo)) return null;
                        string s = e.LerMcdi();
                        return string.IsNullOrEmpty(s) ? null : s.Trim();
                    }
                byte[] q = QuadroId3(strArquivo, "MCDI", "MCI");
                return q == null ? null : McdiDeBytes(q);
            }
            catch { return null; }
        }

        /// <summary>Os bytes do primeiro quadro com um dos nomes, na ID3v2 do começo do arquivo; null se não houver.</summary>
        public static byte[] QuadroId3(string strArquivo, params string[] vetNomes)
        {
            byte[] b;
            using (FileStream fs = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] cab = new byte[10];
                if (fs.Read(cab, 0, 10) < 10 || cab[0] != 'I' || cab[1] != 'D' || cab[2] != '3') return null;
                int intTam = ((cab[6] & 0x7F) << 21) | ((cab[7] & 0x7F) << 14) | ((cab[8] & 0x7F) << 7) | (cab[9] & 0x7F);
                b = new byte[Math.Min(intTam, 8 * 1024 * 1024) + 10];
                Array.Copy(cab, b, 10);
                int n = 10, r;
                while (n < b.Length && (r = fs.Read(b, n, b.Length - n)) > 0) n += r;
            }
            int v = b[3], i = 10;
            while (i + 10 <= b.Length)
            {
                string strId; int intTamQ, intCab;
                if (v == 2) { strId = Encoding.ASCII.GetString(b, i, 3); intTamQ = (b[i + 3] << 16) | (b[i + 4] << 8) | b[i + 5]; intCab = 6; }
                else
                {
                    strId = Encoding.ASCII.GetString(b, i, 4); intCab = 10;
                    intTamQ = v == 4 ? ((b[i + 4] & 0x7F) << 21) | ((b[i + 5] & 0x7F) << 14) | ((b[i + 6] & 0x7F) << 7) | (b[i + 7] & 0x7F)
                                     : (b[i + 4] << 24) | (b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7];
                }
                if (strId[0] == '\0' || intTamQ <= 0 || i + intCab + intTamQ > b.Length) break;
                if (vetNomes.Contains(strId.TrimEnd('\0', ' ')))
                {
                    byte[] q = new byte[intTamQ];
                    Array.Copy(b, i + intCab, q, 0, intTamQ);
                    return q;
                }
                i += intCab + intTamQ;
            }
            return null;
        }

        /// <summary>O conteúdo de um quadro MCDI como texto WM/MCDI: o texto UTF-16 que o WMP grava, ou o CDROM_TOC binário convertido.</summary>
        public static string McdiDeBytes(byte[] q)
        {
            if (q.Length >= 6 && q[1] == 0 && q.Length % 2 == 0)
            {
                string s = Encoding.Unicode.GetString(q).Replace("﻿", "").TrimEnd('\0').Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9A-Fa-f]+(\+[0-9A-Fa-f]+){2,}$")) return s;
            }
            // CDROM_TOC: 2 bytes de tamanho, primeira e última faixa, e 8 bytes por faixa (+ lead-out, faixa 0xAA)
            if (q.Length < 4 + 16) return null;
            int intPrimeira = q[2], intUltima = q[3];
            List<int> lstEnd = new List<int>();
            bool blnMsf = true;
            for (int k = 4; k + 8 <= q.Length; k += 8)
                if (q[k + 4] != 0 || q[k + 5] >= 100 || q[k + 6] >= 60 || q[k + 7] >= 75) blnMsf = false;
            for (int k = 4; k + 8 <= q.Length; k += 8)
            {
                int intEnd = blnMsf ? (q[k + 5] * 60 + q[k + 6]) * 75 + q[k + 7]   // MSF já conta os 2 s (150 setores)
                                    : ((q[k + 4] << 24) | (q[k + 5] << 16) | (q[k + 6] << 8) | q[k + 7]) + 150;
                lstEnd.Add(intEnd);
                if (q[k + 2] == 0xAA) break;
            }
            int intFaixas = intUltima - intPrimeira + 1;
            if (intFaixas <= 0 || lstEnd.Count < intFaixas + 1) return null;
            StringBuilder sb = new StringBuilder(intFaixas.ToString("X"));
            for (int k = 0; k <= intFaixas; k++) sb.Append('+').Append(lstEnd[k].ToString("X"));
            return sb.ToString();
        }

        private static string TextoDoQuadro(byte[] b, int intIni, int intTam)
        {
            byte enc = b[intIni];
            string s;
            if (enc == 1 || enc == 2) s = Encoding.Unicode.GetString(b, intIni + 1, intTam - 1);   // UTF-16 (o BOM vira U+FEFF e sai abaixo)
            else if (enc == 3) s = Encoding.UTF8.GetString(b, intIni + 1, intTam - 1);
            else s = Encoding.GetEncoding(28591).GetString(b, intIni + 1, intTam - 1);
            if (enc == 1 && intTam >= 3 && b[intIni + 1] == 0xFE && b[intIni + 2] == 0xFF) s = Encoding.BigEndianUnicode.GetString(b, intIni + 3, intTam - 3);
            return s.Replace("﻿", "").Trim('\0', ' ');
        }

        /// <summary>
        /// SHA-256 só dos bytes de áudio: pula a(s) ID3v2 do começo e as tags
        /// do fim (ID3v1, APEv2). Gravar tag muda o arquivo, nunca isto.
        /// </summary>
        public static string HashDoAudio(string strArquivo)
        {
            return HashDoAudio(strArquivo, EhWma(strArquivo));
        }

        /// <summary>
        /// Com o formato explícito: a cópia "faixa.wma.arca-partial" não tem a
        /// extensão do formato, e conferi-la como MP3 dava hash diferente com o
        /// áudio intacto (sonda S7, 20/11/2026).
        /// </summary>
        public static string HashDoAudio(string strArquivo, bool blnWma)
        {
            if (blnWma) return HashDoAudioAsf(strArquivo);
            byte[] b = File.ReadAllBytes(strArquivo);
            long inicio = 0;
            while (inicio + 10 <= b.Length && b[inicio] == 'I' && b[inicio + 1] == 'D' && b[inicio + 2] == '3')
            {
                long tam = ((b[inicio + 6] & 0x7F) << 21) | ((b[inicio + 7] & 0x7F) << 14) |
                           ((b[inicio + 8] & 0x7F) << 7) | (b[inicio + 9] & 0x7F);
                inicio += 10 + tam + ((b[inicio + 5] & 0x10) != 0 ? 10 : 0);
            }
            // Tudo o que é tag no fim, quantas houver e em qualquer ordem: há MP3 com
            // várias ID3v1 empilhadas (programa antigo que acrescentava uma a cada
            // edição), e o taglib-sharp, ao gravar, deixa uma só. Contar as de
            // sobra como áudio dava "a cópia mudou o áudio" com o áudio intacto
            // ("Une nuit d'amour", Slaï, 20/11/2026).
            long fim = b.Length;
            while (true)
            {
                if (fim - 128 >= inicio && b[fim - 128] == 'T' && b[fim - 127] == 'A' && b[fim - 126] == 'G') { fim -= 128; continue; }
                if (fim - 32 >= inicio && Encoding.ASCII.GetString(b, (int)(fim - 32), 8) == "APETAGEX")
                {
                    int tamApe = BitConverter.ToInt32(b, (int)(fim - 20));
                    int flags = BitConverter.ToInt32(b, (int)(fim - 12));
                    long novo = fim - tamApe - ((flags & unchecked((int)0x80000000)) != 0 ? 32 : 0);
                    if (novo >= inicio && novo < fim) { fim = novo; continue; }
                }
                if (fim - 15 >= inicio && Encoding.ASCII.GetString(b, (int)(fim - 9), 9) == "LYRICS200")
                {
                    int tamLyr;
                    if (int.TryParse(Encoding.ASCII.GetString(b, (int)(fim - 15), 6), out tamLyr) && fim - 15 - tamLyr >= inicio)
                    { fim = fim - 15 - tamLyr; continue; }
                }
                if (fim - 10 >= inicio && b[fim - 10] == '3' && b[fim - 9] == 'D' && b[fim - 8] == 'I')   // rodapé de ID3v2 no fim
                {
                    long tam = ((b[fim - 4] & 0x7F) << 21) | ((b[fim - 3] & 0x7F) << 14) | ((b[fim - 2] & 0x7F) << 7) | (b[fim - 1] & 0x7F);
                    long novo = fim - 20 - tam;
                    if (novo >= inicio) { fim = novo; continue; }
                }
                break;
            }
            if (fim < inicio) fim = inicio;
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(b, (int)inicio, (int)(fim - inicio))).Replace("-", "").ToLowerInvariant();
        }

        public bool blnWma { get { return EhWma(strCaminho); } }

        public static bool EhWma(string strArquivo)
        {
            string e = Path.GetExtension(strArquivo ?? "").ToLowerInvariant();
            return e == ".wma" || e == ".asf";
        }

        private static readonly Guid GUID_ASF_CABECALHO = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C");
        private static readonly Guid GUID_ASF_DADOS = new Guid("75B22636-668E-11CF-A6D9-00AA0062CE6C");

        /// <summary>
        /// WMA: SHA-256 do Data Object do ASF inteiro (os pacotes de áudio). As
        /// tags moram no Header Object, antes dele; o índice, depois. Gravar
        /// tag muda os dois, nunca isto.
        /// </summary>
        private static string HashDoAudioAsf(string strArquivo)
        {
            using (FileStream fs = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if (fs.Length < 30 || new Guid(br.ReadBytes(16)) != GUID_ASF_CABECALHO)
                    throw new InvalidDataException("não é um arquivo ASF/WMA");
                long pos = (long)br.ReadUInt64();   // o Data Object vem logo depois do cabeçalho
                while (pos + 24 <= fs.Length)
                {
                    fs.Position = pos;
                    Guid g = new Guid(br.ReadBytes(16));
                    long tam = (long)br.ReadUInt64();
                    if (tam < 24) break;
                    if (g == GUID_ASF_DADOS)
                    {
                        if (pos + tam > fs.Length) tam = fs.Length - pos;   // gravação ao vivo: tamanho 0/aberto
                        fs.Position = pos;
                        using (SHA256 sha = SHA256.Create())
                        {
                            byte[] buf = new byte[1 << 20];
                            long resta = tam;
                            while (resta > 0)
                            {
                                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, resta));
                                if (n <= 0) break;
                                sha.TransformBlock(buf, 0, n, null, 0);
                                resta -= n;
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                        }
                    }
                    pos += tam;
                }
                throw new InvalidDataException("o WMA não tem Data Object");
            }
        }

        /// <summary>Ordem do disco: pelo número da faixa, se todas têm e não repetem; senão, pelo nome do arquivo.</summary>
        public static List<TFaixaMp3> Ordenar(List<TFaixaMp3> lst)
        {
            bool blnNumeracaoBoa = lst.All(f => f.intFaixaTag > 0) && lst.Select(f => f.intFaixaTag).Distinct().Count() == lst.Count;
            return blnNumeracaoBoa
                ? lst.OrderBy(f => f.intFaixaTag).ToList()
                : lst.OrderBy(f => f.strArquivo, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
