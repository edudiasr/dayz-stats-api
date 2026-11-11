# dayz-stats-api

API em ASP.NET Core que importa os logs `.ADM` de um servidor de DayZ para um banco SQLite e expõe as estatísticas: jogadores, tempo online, mortes, kills e rankings.

É a continuação do [dayz-log-analyzer](https://github.com/edudiasr/dayz-log-analyzer), que faz o resumo no terminal. Aqui os dados ficam guardados e dá pra consultar por HTTP: um bot do Discord, um site com ranking ou um painel de admin podem usar a API sem precisar ler log nenhum.

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| GET | `/api/stats` | resumo do servidor: jogadores, mortes, kills PvP e da IA, horas jogadas, causas de morte |
| GET | `/api/players?search=&page=&pageSize=` | lista de jogadores com busca por nome |
| GET | `/api/players/{id}` | kills, mortes, K/D, sessões e últimas mortes de um jogador |
| GET | `/api/leaderboard?by=kills\|deaths\|playtime&top=` | ranking |
| GET | `/api/deaths?cause=&playerId=&from=&to=&page=` | mortes com filtros |
| POST | `/api/import` | importa os `.ADM` novos da pasta configurada |
| POST | `/api/import/file` | importa um `.ADM` enviado por upload (`multipart/form-data`, campo `file`) |
| GET | `/health` | pra saber se a API está de pé |

A documentação interativa fica em `/scalar` (gerada a partir do OpenAPI em `/openapi/v1.json`).

Exemplo, com o log da pasta `examples` (nomes e ids inventados):

```
GET /api/leaderboard?by=playtime&top=3

[
  {"rank":1,"playerId":"LoBo7xQ2...","name":"Lobo","value":1.55},
  {"rank":2,"playerId":"cRv1Lm...","name":"Corvo","value":0.57},
  {"rank":3,"playerId":"aX9kP3...","name":"Survivor","value":0.52}
]
```

```
GET /api/deaths?cause=jogador

{
  "items": [
    {"id":3,"time":"2026-11-09T00:00:41","playerName":"Survivor","cause":"jogador",
     "killerName":"Lobo","weapon":"Mosin9130","distance":462.3, ...},
    {"id":1,"time":"2026-11-08T23:10:35","playerName":"Raposa","cause":"jogador",
     "killerName":"Lobo","weapon":"AKM","distance":134.7, ...}
  ],
  "page":1, "pageSize":20, "total":2
}
```

Parâmetro inválido (`?by=dano`, `?page=0`, `pageSize` acima de 100) volta 400 no formato ProblemDetails, e jogador que não existe volta 404.

## Rodando

Precisa do .NET 10 SDK.

```
dotnet run --project src/DayzStats.Api
```

Configuração no `appsettings.json` ou por variável de ambiente:

| Chave | Variável | Pra que serve |
|---|---|---|
| `ConnectionStrings:Stats` | `ConnectionStrings__Stats` | arquivo do SQLite (padrão `dayzstats.db`) |
| `Import:Folder` | `Import__Folder` | pasta `profiles` do servidor |
| `Import:ApiKey` | `Import__ApiKey` | se preenchida, os POST de importação exigem o header `X-Api-Key` |

Depois é só chamar `POST /api/import` (pode ser num agendador, a cada restart do servidor). Arquivo que já foi importado é pulado, então chamar de novo não duplica nada.

## Testes

```
dotnet test --solution DayzStats.slnx
```

Tem dois tipos:

- **parser** (`AdmParserTests`): casos tirados de logs reais, como a mesma morte escrita em três linhas, morte pela IA, queda, jogador que reconecta com outro nome, virada da meia-noite e restart.
- **API** (`ApiTests`): sobe a aplicação inteira com `WebApplicationFactory` e um SQLite em memória, importa o log de exemplo e testa os endpoints por HTTP, incluindo a chave de API, os 400 e os 404.

Rodam no GitHub Actions a cada push.

## Algumas decisões

- **Minimal API com `TypedResults`.** Cada endpoint declara no tipo de retorno o que pode devolver (`Results<Ok<...>, NotFound>`), e o OpenAPI sai certo sem anotação extra.
- **Jogador é o id do log, não o nome.** No meu servidor tem vários "Survivor" diferentes, e o mesmo jogador às vezes aparece como "Nome (2)".
- **Uma morte, várias linhas.** O DayZ escreve a mesma morte em até três linhas, em ordem variada. O parser marca o jogador como morto até ele conectar de novo, e as outras linhas só completam os dados.
- **Importação idempotente.** O nome de cada arquivo importado fica salvo (índice único). Cada arquivo entra numa transação: ou entra tudo, ou nada.
- **`CultureInfo.InvariantCulture` nas distâncias.** Num Windows em português, `double.Parse("41.09")` sem isso lê o ponto como separador de milhar.
- **Consultas.** Filtro, ordem e paginação são feitos na entidade antes de montar o DTO, senão o EF Core não consegue traduzir pra SQL.
- **`EnsureCreated` em vez de migrations.** O banco é local, de um processo só, e o esquema é pequeno. Se crescer, troco por migrations.
- **Comparação da chave** com `CryptographicOperations.FixedTimeEquals`, pra não vazar nada pelo tempo de resposta.

Com os 26 dias de log do meu servidor, a importação leva cerca de 1 segundo e os números batem com os do dayz-log-analyzer.

## Estrutura

```
src/DayzStats.Api/
  Parsing/AdmParser.cs        leitura do .ADM (regex gerada em tempo de compilação)
  Data/                       entidades e DbContext (EF Core + SQLite)
  Import/LogImporter.cs       grava no banco, sem importar o mesmo arquivo duas vezes
  Endpoints/                  rotas, DTOs e validação
  Program.cs
tests/DayzStats.Api.Tests/    xUnit v3
examples/                     log de exemplo
```

---

Eduardo Dias · [portfólio](https://edudiasr.github.io/portfolio-web/) · [LinkedIn](https://www.linkedin.com/in/eduardo-dias-99b90a44a)
