# Visualizador de Imagens

Visualizador de imagens leve para Windows 11, inspirado no antigo Visualizador de Fotos do Windows, com a interface Fluent do Windows 11 (tema claro/escuro e cor de destaque do sistema).

## Recursos

- Navegação entre as imagens da pasta, na mesma ordem do Explorer ("foto 2" antes de "foto 10")
- Zoom com a roda do mouse no ponto do cursor, duplo clique para alternar entre ajustar à janela e 100%, arrastar para mover
- Excluir manda para a Lixeira, com confirmação
- Girar, copiar, abrir no Explorer, propriedades do arquivo, tela inteira
- Respeita a orientação EXIF das fotos de celular
- Carregamento rápido: decodifica no tamanho da tela e só carrega a resolução total ao ampliar
- Pré-carrega a pasta em segundo plano (cache temporário em memória, até 512 MB, expira 10 minutos após o último uso), ideal para pastas no Google Drive e na rede
- Formatos: JPG, PNG, BMP, GIF, TIFF, ICO, WEBP, HEIC/HEIF e AVIF (com as extensões do Windows), JXR

## Instalação

Baixe o `.msi` na página de [Releases](../../releases) e execute. O instalador:

1. instala em `C:\Program Files\Visualizador de Imagens`;
2. cria o atalho no Menu Iniciar;
3. registra o app em **Configurações → Aplicativos → Aplicativos padrão** e abre essa página no fim, para você defini-lo como padrão.

Requer o [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64).

Para desinstalar: **Configurações → Aplicativos → Aplicativos instalados**.

## Atalhos

| Tecla | Ação |
| --- | --- |
| ← / → ou PgUp / PgDn | Imagem anterior / próxima |
| Home / End | Primeira / última imagem |
| Roda do mouse | Zoom no ponto do cursor |
| Duplo clique | Ajustar à janela / tamanho real |
| + / - | Ampliar / reduzir |
| Ctrl+0 / Ctrl+1 | Ajustar à janela / tamanho real |
| Ctrl+, / Ctrl+. | Girar para a esquerda / direita |
| Del | Excluir (Lixeira) |
| Ctrl+C | Copiar |
| Ctrl+O | Abrir |
| Alt+Enter | Propriedades |
| F11 / Esc | Tela inteira |

## Compilação

Requer o [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
dotnet publish -c Release -o dist
```

Gera `dist\ImageViewer.exe` (arquivo único, ~330 KB, depende do .NET 9 Desktop Runtime).

### Instalador

Requer o [WiX Toolset v5](https://wixtoolset.org/):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2
.\Installer\build.ps1
```

Gera `dist\VisualizadorDeImagens-<versão>-x64.msi`.

## Licença

[MIT](LICENSE)
