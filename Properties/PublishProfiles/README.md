# Perfis de Publicação

Este diretório contém os perfis de publicação do projeto.

## IISProfile.pubxml

Perfil de publicação configurado para publicação local (pasta `publish`). Ideal para publicar e depois copiar manualmente para o IIS ou testar localmente.

### Como usar:

1. **Publicar usando o perfil:**
   ```powershell
   dotnet publish /p:PublishProfile=IISProfile
   ```

2. **Depois de publicar, copie os arquivos para o IIS:**
   - Copie todo o conteúdo da pasta `publish` para `C:\inetpub\wwwroot\PlateAnalysisApi`
   - Ou execute como administrador e use o perfil `IISProfileDirect`

### Configurações do perfil:

- **PublishDir**: `.\publish` (pasta local no projeto)
- **RuntimeIdentifier**: `win-x64` (Windows 64-bit)
- **SelfContained**: `true` (inclui o runtime do .NET)
- **PublishReadyToRun**: `true` (melhora performance)
- **DeleteExistingFiles**: `true` (remove arquivos antigos antes de publicar)

## IISProfileDirect.pubxml

Perfil de publicação que publica diretamente no diretório do IIS. **Requer execução como administrador**.

### Como usar:

1. **Abra o PowerShell como administrador:**
   - Clique com botão direito no PowerShell → "Executar como administrador"

2. **Navegue até o diretório do projeto e publique:**
   ```powershell
   cd C:\Projeto\analiseplaca
   dotnet publish /p:PublishProfile=IISProfileDirect
   ```

### Configurações do perfil:

- **PublishDir**: `C:\inetpub\wwwroot\PlateAnalysisApi` (diretório do IIS)
- **RuntimeIdentifier**: `win-x64` (Windows 64-bit)
- **SelfContained**: `true` (inclui o runtime do .NET)
- **PublishReadyToRun**: `true` (melhora performance)
- **DeleteExistingFiles**: `true` (remove arquivos antigos antes de publicar)

### Configuração no IIS:

1. Instale o **ASP.NET Core Hosting Bundle** (se ainda não instalado):
   - Download: https://dotnet.microsoft.com/download/dotnet/8.0
   - Procure por "Hosting Bundle" na página

2. Crie o site no IIS Manager:
   - Abra o IIS Manager
   - Clique com botão direito em "Sites" → "Add Website"
   - **Site name**: `PlateAnalysisApi`
   - **Physical path**: `C:\inetpub\wwwroot\PlateAnalysisApi`
   - **Binding**: Escolha a porta (ex: 8080) ou deixe o padrão (80)

3. Configure o Application Pool:
   - Selecione o Application Pool criado para o site
   - **.NET CLR Version**: "No Managed Code"
   - **Managed Pipeline Mode**: "Integrated"

4. O arquivo `web.config` será copiado automaticamente durante a publicação.

### Notas:

- O perfil publica para `C:\inetpub\wwwroot\PlateAnalysisApi` por padrão
- Se quiser publicar em outro diretório, edite o arquivo `IISProfile.pubxml` e altere o valor de `PublishDir`
- Certifique-se de que o diretório de destino existe e o IIS tem permissões de leitura/execução

