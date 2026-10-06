using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dante.Application.MetricasDoBrain;
using Microsoft.Extensions.Configuration;
namespace Dante.Infrastructure.MetricasDoBrain;

// JSONL local e só de acréscimo (#148): uma métrica por linha, sem conteúdo; fora do banco canônico, do backup e da
// exportação do Brain. DANTE_BRAIN_METRICS_FILE troca o caminho padrão ~/.dante/brain/metricas.jsonl.
public sealed class RegistroDeMetricasEmArquivo(IConfiguration configuration) : IRegistroDeMetricasDoBrain
{
    private static readonly JsonSerializerOptions Json=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    private readonly SemaphoreSlim gate=new(1,1);
    public string Caminho=>configuration["DANTE_BRAIN_METRICS_FILE"] is { Length: > 0 } caminho?Path.GetFullPath(caminho):
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".dante","brain","metricas.jsonl");
    public async Task RegistrarAsync(MetricaDoBrainDto metrica,CancellationToken cancellationToken=default)
    {
        var linha=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metrica,Json)+"\n");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            var options=new FileStreamOptions{Mode=FileMode.Append,Access=FileAccess.Write,Share=FileShare.Read};
            if(!OperatingSystem.IsWindows()) options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using var stream=new FileStream(Caminho,options);
            await stream.WriteAsync(linha,cancellationToken);
        }
        finally{gate.Release();}
    }
    public async Task<IReadOnlyList<MetricaDoBrainDto>> ListarAsync(Guid idTenant,Guid idUsuario,Guid idEspaco,Guid? idProjeto,CancellationToken cancellationToken=default)
    {
        string[] linhas;
        await gate.WaitAsync(cancellationToken);
        try{linhas=File.Exists(Caminho)?await File.ReadAllLinesAsync(Caminho,cancellationToken):[];}
        finally{gate.Release();}
        var metricas=new List<MetricaDoBrainDto>();
        foreach(var linha in linhas)
        {
            MetricaDoBrainDto? metrica;
            try{metrica=JsonSerializer.Deserialize<MetricaDoBrainDto>(linha,Json);}
            catch(JsonException){continue;}
            if(metrica is not null&&metrica.IdTenant==idTenant&&metrica.IdUsuario==idUsuario&&metrica.IdEspacoDeConhecimento==idEspaco&&metrica.IdProjeto==idProjeto)
                metricas.Add(metrica);
        }
        return metricas;
    }
}
