# Third-Party Attributions

VerseOps is licensed under the [MIT License](LICENSE) and is **not a Microsoft
product**. It is an independent, open-source operations console authored by a
Microsoft employee in a personal capacity. Microsoft Corporation does not
sponsor, endorse, support, or warrant this project.

The names, logos, product marks, and service icons referenced below are
trademarks of their respective owners and are used here solely for nominative
identification of the platforms VerseOps inspects.

---

## Microsoft brand assets bundled in `VerseOps.App/Assets/MicrosoftIcons/`

| Folder           | Source                                                                                                       | Items used                                                                                          |
| ---------------- | ------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------- |
| `PowerPlatform/` | [Power Platform scalable icons](https://aka.ms/PowerPlatformIcons) (Microsoft, public download)              | `PowerPlatform`, `PowerApps`, `PowerAutomate`, `PowerPages`, `Dataverse`, `CopilotStudio`, `AIBuilder`, `Agent365` |
| `D365/`          | [Dynamics 365 scalable icons](https://aka.ms/Dynamics365Icons) (Microsoft, public download)                  | All 16 D365 app marks (Sales, Customer Service, Field Service, Finance, …)                          |
| `Entra/`         | [Microsoft Entra architecture icons – Oct 2023](https://aka.ms/EntraIcons) (Microsoft, public download)      | All 14 Entra family + product marks (color and BW)                                                  |
| `Azure/`         | [Azure architecture icons V23](https://learn.microsoft.com/azure/architecture/icons/) (Microsoft, public)    | `Storage-Accounts`, `Key-Vaults`, `Monitor`, `Resource-Groups`, `Subscriptions`, `App-Services`, `API-Management-Services` |

These icons remain the trademarks of Microsoft Corporation. They are
redistributed in this repository under the terms published by Microsoft on the
download pages above (Microsoft Trademark and Brand Guidelines), which permit
identification of the platforms an application interoperates with. They are
**not** licensed under the MIT License that covers the rest of this codebase.

If you fork VerseOps and rebrand it for a non-Microsoft platform, remove the
`Assets/MicrosoftIcons/` folder and the `SharpVectors.Reloaded` package
reference in `VerseOps.App/VerseOps.App.csproj`.

---

## Third-party libraries

| Package                       | License | Purpose                                                                  |
| ----------------------------- | ------- | ------------------------------------------------------------------------ |
| [WPF-UI](https://github.com/lepoco/wpfui)             | MIT     | Fluent v2 controls (`FluentWindow`, `TitleBar`, `SymbolIcon` glyphs)     |
| [SharpVectors.Reloaded](https://github.com/ElinamLLC/SharpVectors) | BSD-3-Clause / MIT | Renders the Microsoft brand SVGs natively as WPF `Drawing` at runtime    |
| [Microsoft.Identity.Client](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) | MIT     | Interactive system-browser + broker auth                                 |
| [Microsoft.PowerPlatform.Management](https://www.nuget.org/packages/Microsoft.PowerPlatform.Management) | Microsoft EULA | PPAC SDK — environment + capacity discovery                              |
| [Microsoft.Kiota.\*](https://github.com/microsoft/kiota)             | MIT     | OData client plumbing                                                    |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT     | Local catalog cache (`%LOCALAPPDATA%\VerseOps\inventory.db`)             |

Full per-package license text is reproduced in each NuGet package under
`%USERPROFILE%\.nuget\packages\<id>\<version>\`.

---

## Trademarks

Microsoft, Power Platform, Power Apps, Power Automate, Power Pages, Dataverse,
Copilot Studio, Dynamics 365, Microsoft Entra, Microsoft 365, Azure, and the
related product marks are trademarks of the Microsoft group of companies.

All other trademarks are property of their respective owners.
