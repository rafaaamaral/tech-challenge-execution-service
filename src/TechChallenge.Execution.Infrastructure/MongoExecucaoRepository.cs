using MongoDB.Driver;
using TechChallenge.Execution.Application;
using TechChallenge.Execution.Domain;

namespace TechChallenge.Execution.Infrastructure;

public class MongoExecucaoRepository : IExecucaoRepository
{
    private readonly IMongoCollection<Execucao> _collection;

    public MongoExecucaoRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<Execucao>("execucoes");
        var index = new CreateIndexModel<Execucao>(
            Builders<Execucao>.IndexKeys.Ascending(e => e.OrdemServicoUniqueCode),
            new CreateIndexOptions { Unique = true, Name = "ux_ordem_servico_unique_code" });
        _collection.Indexes.CreateOne(index);
    }

    public async Task<Execucao?> ObterPorOrdemServicoAsync(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        await _collection.Find(e => e.OrdemServicoUniqueCode == ordemServicoUniqueCode).FirstOrDefaultAsync(ct);

    public async Task<bool> ExistePorOrdemServicoAsync(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        await _collection.Find(e => e.OrdemServicoUniqueCode == ordemServicoUniqueCode).AnyAsync(ct);

    public async Task InserirAsync(Execucao execucao, CancellationToken ct)
    {
        try
        {
            await _collection.InsertOneAsync(execucao, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicidadeException("Já existe execução para esta ordem de serviço.");
        }
    }

    public Task AtualizarAsync(Execucao execucao, CancellationToken ct) =>
        _collection.ReplaceOneAsync(e => e.Id == execucao.Id, execucao, cancellationToken: ct);
}
