using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Google;

// Refresh token nunca em texto puro (#224): a credencial é cifrada com AES-256-GCM em credencial.bin, com a chave em
// um arquivo separado (ou em DANTE_GOOGLE_KEY), ambos 0600 num diretório 0700 fora de qualquer checkout, appsettings
// ou .env. A proteção efetiva é a do usuário do sistema operacional: quem lê os dois arquivos como o usuário do
// D.A.N.T.E. obtém o token; o que se evita é o token legível em claro, em cópias, buscas ou logs.
public sealed class GoogleCredentialStore(string diretorio, byte[]? chaveExterna = null)
{
    private const byte Versao = 1;
    private static readonly byte[] Contexto = "dante-google-credencial-v1"u8.ToArray();
    private readonly object gate = new();

    public string Diretorio { get; } = diretorio;
    public string Arquivo => Path.Combine(Diretorio, "credencial.bin");
    private string ArquivoDaChave => Path.Combine(Diretorio, "chave");

    public bool Existe => File.Exists(Arquivo);

    public CredencialGoogle? Carregar()
    {
        lock (gate)
        {
            if (!File.Exists(Arquivo)) return null;
            try
            {
                var dados = File.ReadAllBytes(Arquivo);
                var chave = Chave(criar: false) ?? throw new CryptographicException("Chave ausente.");
                if (dados.Length < 1 + 12 + 16 || dados[0] != Versao) throw new CryptographicException("Formato desconhecido.");
                var nonce = dados.AsSpan(1, 12);
                var tag = dados.AsSpan(13, 16);
                var cifrado = dados.AsSpan(29);
                var texto = new byte[cifrado.Length];
                using var aes = new AesGcm(chave, 16);
                aes.Decrypt(nonce, cifrado, tag, texto, Contexto);
                return JsonSerializer.Deserialize<CredencialGoogle>(texto) ?? throw new CryptographicException("Credencial vazia.");
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or FormatException)
            {
                throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoConectada,
                    "A credencial Google local está ilegível; desconecte e conecte a conta novamente.");
            }
        }
    }

    public void Salvar(CredencialGoogle credencial)
    {
        ArgumentNullException.ThrowIfNull(credencial);
        lock (gate)
        {
            CriarDiretorio();
            var chave = Chave(criar: true)!;
            var texto = JsonSerializer.SerializeToUtf8Bytes(credencial);
            var dados = new byte[1 + 12 + 16 + texto.Length];
            dados[0] = Versao;
            RandomNumberGenerator.Fill(dados.AsSpan(1, 12));
            using (var aes = new AesGcm(chave, 16))
                aes.Encrypt(dados.AsSpan(1, 12), texto, dados.AsSpan(29), dados.AsSpan(13, 16), Contexto);
            CryptographicOperations.ZeroMemory(texto);
            EscreverPrivado(Arquivo, dados);
        }
    }

    // Apaga credencial e chave; true quando havia credencial.
    public bool Apagar()
    {
        lock (gate)
        {
            var existia = File.Exists(Arquivo);
            File.Delete(Arquivo);
            if (chaveExterna is null) File.Delete(ArquivoDaChave);
            return existia;
        }
    }

    private byte[]? Chave(bool criar)
    {
        if (chaveExterna is not null)
            return chaveExterna.Length == 32 ? chaveExterna : throw new CryptographicException("DANTE_GOOGLE_KEY deve ter 32 bytes.");
        if (File.Exists(ArquivoDaChave))
        {
            var existente = File.ReadAllBytes(ArquivoDaChave);
            return existente.Length == 32 ? existente : throw new CryptographicException("Arquivo de chave inválido.");
        }
        if (!criar) return null;
        var nova = RandomNumberGenerator.GetBytes(32);
        EscreverPrivado(ArquivoDaChave, nova);
        return nova;
    }

    private void CriarDiretorio()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Diretorio);
        else Directory.CreateDirectory(Diretorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Escrita atômica: o arquivo temporário já nasce 0600 e substitui o anterior de uma vez.
    private static void EscreverPrivado(string caminho, byte[] dados)
    {
        var temporario = caminho + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporario, options)) stream.Write(dados);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporario, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporario, caminho, overwrite: true);
    }
}
