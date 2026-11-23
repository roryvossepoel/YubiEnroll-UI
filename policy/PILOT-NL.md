# Passkey Desk voor YubiKeys · Policy Preview 11

Dit is de afzonderlijke Windows x64-testversie **0.11.0-policy.11**, gebaseerd op 0.10.9. Pak het ZIP-bestand uit in een eigen map en start `PasskeyDesk.exe`. Laat `PasskeyDesk.ConsoleHost.exe` ernaast staan. De templates worden niet automatisch geïnstalleerd. De vereisten voor .NET en de Yubico-tools zijn gelijk aan 0.10.9.

## Indeling van de template (revisie 1.4)

Onder **Passkey Desk for YubiKeys** staan twee onderdelen. Gebruik voor deze test de EXE van Policy Preview 11 en de meegeleverde ADMX en ADML (revisie 1.4).

| Onderdeel | Wat configureer je? | Waar komt het terecht? |
| --- | --- | --- |
| YubiEnroll CLI configuration (TOML) → Tenants | Entra-tenants via JSON | De providers in de TOML van het uitvoerende account. |
| YubiEnroll CLI configuration (TOML) → Configuration profiles | Vijf mogelijke profielslots, inclusief PIN genereren en wijzigen bij eerste gebruik | De profielen in diezelfde TOML; de zelfstandige CLI kan ze gebruiken. |
| Passkey Desk app settings → Logging → Activity logging | Activiteit bewaren, bewaartermijn en logmap | Alleen de app. Bij een leeg pad: `Documenten\YubiKey Desk\ActivityLog`. |
| Passkey Desk app settings → Logging → Registration logging | Registraties bewaren, bewaartermijn, registratiemap en gegenereerde initiële PINs opnemen (standaard uit) | Alleen de app. Bij een leeg pad: `Documenten\YubiKey Desk\RegistrationLog`. PIN-logging geldt ook voor CSV-export van de betreffende records. |
| Passkey Desk app settings → Terminal preference | Interne of externe terminal | Alleen de app. |

**Bewaartermijn voor registraties:** stel bij *Registration logging → Keep registrations for (days)* 1–3650 dagen in (standaard 30). Met registratie-logging aan worden bij het starten, bij opslaan van de instelling en op een volgende dag bij lezen/schrijven verlopen registratiebestanden opgeruimd. De huidige UTC-dag telt mee. CSV-exports blijven staan. Beide logboeken hebben een eigen DWORD-waarde `RetentionDays` in hun eigen subsleutel.

Met **Documenten** wordt de Windows-map van het uitvoerende account bedoeld, ook als die is omgeleid. Je kunt beide logpaden afzonderlijk instellen. Elke ingestelde policy vergrendelt alleen de bijbehorende groep. Alleen logging of een terminalpolicy maakt of wijzigt geen TOML en laat tenants en profielen bewerkbaar.

Bij het opstarten toont **Before you start → YubiEnroll CLI → Organization policy** de uitkomst: TOML aangemaakt, bijgewerkt of al actueel. Bij een lege tenantpolicy zonder bestand is geen bestand nodig. Bij alleen appbeleid staat er dat app-instellingen zijn toegepast zonder TOML-synchronisatie. Zonder bekende policy ontbreekt de substap. Bij gedetecteerd beleid staat de uitkomst één keer per start in **Settings → Activity log**. Staat het bewaren van activiteit uit, dan is de regel alleen tijdens die sessie zichtbaar. Terugkeren naar dit scherm voert geen nieuwe synchronisatie uit.

## Begin met één testapparaat

1. **Nulmeting, zonder policy:** sluit andere instanties van Passkey Desk en de CLI. Gebruik een testaccount en maak een kopie van de bestaande `yubienroll.toml`. Start de pilot. Tenants en profielen moeten gewoon bewerkbaar zijn. Sluit zonder wijzigingen: inhoud en bestandstijd van de TOML moeten gelijk blijven.
2. **Importeer** `PasskeyDesk.admx` en `en-US/PasskeyDesk.adml` via **Devices → Manage devices → Configuration → Import ADMX**. Wacht op **Available**. Importeren alleen activeert niets. Maak daarna een Windows-configuratie met **Templates → Imported Administrative templates (Preview)**. Wijs uitsluitend toe aan je pilottestgroep.
3. **Test eerst zonder de TOML te wijzigen:** configureer alleen **Terminal preference → In-app terminal**. Synchroniseer het apparaat en herstart de app zodra de policy is aangekomen. Onder Settings moet **Managed by your organization** verschijnen. Alleen de terminalkeuze moet vaststaan. Beide loggroepen en het toevoegen, wijzigen, verwijderen en importeren van tenants en profielen blijven beschikbaar. Ook **External console** telt als aanwezige policy.
4. **Test centrale configuratie:** schakel **Entra tenants (JSON)** in. Gebruik `tenants.example.json` als vormvoorbeeld; vervang de twee fictieve GUID's en controleer de redirect-URI tegen je eigen appregistratie. Configureer eventueel één of meer van **Configuration profile 1–5**. Synchroniseer, sluit de app en open haar opnieuw onder hetzelfde Windows-account. Controleer de lijsten in de app en buiten de app met `yubienroll providers list` en `yubienroll profiles list`. Hiervoor is geen inschrijving of FIDO-reset nodig.
5. **Negatieve test:** maak op dit testapparaat de tenant-JSON ongeldig. Na synchronisatie moet de app bij starten de fout melden en afsluiten; de vorige TOML moet ongewijzigd zijn. Herstel de JSON en herstart.
6. **Terug naar lokaal:** zet alle geconfigureerde instellingen op **Not configured** en controleer na synchronisatie of de bijbehorende registerwaarden daadwerkelijk zijn verwijderd. Sluit en heropen de app. Je kunt tenants, profielen en appvoorkeuren weer wijzigen. Een lege registersleutel hoeft niet te worden verwijderd.

[Microsoft-instructies voor importeren en toewijzen](https://learn.microsoft.com/en-us/intune/device-configuration/settings-catalog/import-custom-admx-templates)

## Wat beheert een policy precies?

Bij opstarten leest de app uitsluitend de bekende subsleutels van de **64-bits** registersleutel `HKLM\SOFTWARE\Policies\PasskeyDesk`. De ADMX schrijft de waarden via het beleid; de app leest ze alleen. Gebruik voor deze schone pilot de nieuwe template én EXE, configureer het beleid en synchroniseer het apparaat. Waarden direct onder `PasskeyDesk` uit een eerdere pilot worden genegeerd en niet gemigreerd. Er is geen aan/uit-policy; een lege sleutel of onbekende waarde schakelt policy mode niet in.

| Subsleutel onder `PasskeyDesk` | Registerwaarden |
| --- | --- |
| `YubiEnroll\Tenants` | `TenantsJson` (REG_SZ), dezelfde JSON-array als voorheen |
| `YubiEnroll\Profiles\Profile1` t/m `Profile5` | Per profiel: `Name`, `Reset`, `MinPinLength`, `AlwaysUv`, `RequireEa`, `ForcePinChange`, `RandomPin`, `RandomPinLength` |
| `AppSettings\Logging\Activity` | `Enabled`, `Folder`, `RetentionDays` |
| `AppSettings\Logging\Registration` | `Enabled`, `Folder`, `RetentionDays`, `SaveGeneratedPin` |
| `AppSettings\Terminal` | `EmbeddedTerminal` (0 = externe console, 1 = interne terminal), alleen als je deze policy configureert |

Op **App settings** staat naast elke beheerde sectie een pill **Policy mode: read only**. Laat **Terminal preference** op **Not configured** om operators vrij te laten kiezen, ook als beide logboeken worden beheerd. Na verwijderen van een policy wordt alleen dat onderdeel bij herstart weer bewerkbaar.

| Ingesteld | Effect bij het starten |
| --- | --- |
| Geen | De app werkt direct met dezelfde lokale configuratie als 0.10.9. |
| Entra tenants (JSON) | De array vervangt de volledige lijst Entra-tenants. Vier velden per object: `name`, `tenantId`, `clientId`, `redirectUri`. Geen vast aantal tenantslots; de invoergrootte van het platform geldt wel. Een expliciete lege array `[]` verwijdert de Entra-lijst. |
| Eén of meer profielslots | De ingevulde slots vormen samen de **volledige** profielenlijst. Slot 5 kan ook zelfstandig worden gebruikt. Gebruik unieke namen. Een ongebruikte minimum- of gegenereerde PIN-lengte geef je op als 0, anders 4–63. De zelfstandige CLI 1.2.0 vereist numerieke lengtes in de TOML: bij 0 schrijft de app voor het minimum 4 en voor een niet opgegeven gegenereerde lengte het ingestelde minimum (anders 4). Bij minimum 0 blijft Passkey Desk bij uitgifte de actuele minimumlengte van de sleutel gebruiken. Er staat geen PIN-waarde in de policy. |
| Activity logging / Registration logging | De aangewezen groep loginstellingen geldt zolang de policy aanwezig is. Zonder mappad geldt de standaardmap van het uitvoerende account. Een mappad moet absoluut zijn; omgevingsvariabelen worden voor die gebruiker uitgewerkt. Het loggen van gegenereerde PINs staat standaard uit. |
| Terminal preference | Bepaalt de terminalvoorkeur. Bestaand gedrag blijft gelden: als bij inschrijving een gegenereerde PIN moet worden vastgelegd, gebruikt de app haar eigen terminal. |

Zijn alleen tenants beheerd, dan blijven de lokale profielen bewerkbaar en vervangt import alleen profielen. Zijn alleen profielen beheerd, dan geldt het omgekeerde. Een beheerd tabblad toont rechtsboven **Policy mode: read only** in plaats van de import-/exportknop. De hint benoemt de beheerde lijst. Vanaf het andere tabblad kun je alleen de lokale lijst overzetten. Zijn beide lijsten beheerd, dan is er nergens een knop voor configuratie-import of -export. Geen van deze policies vergrendelt de loggroepen of de terminalkeuze. Een tenantpolicy zonder profielbeleid laat de profielen intact. Bij alleen profielbeleid blijft een lokale Entra-tenant staan; een oude verwijzing naar een nu verdwenen standaardprofiel wordt wel verwijderd. Niet-Entra-providers worden niet door een gelijknamige policytenant overschreven: dat geeft een foutmelding.

## De TOML en terugdraaien

De CLI blijft de gewone TOML van het **Windows-account dat de app uitvoert** gebruiken, doorgaans `%APPDATA%\Yubico\yubienroll\yubienroll.toml`. De app respecteert ook haar bestaande alternatieve zoekpad. Start je verhoogd met een ander account, dan werk je in het profiel van dat account.

De app valideert alle ontvangen instellingen voordat zij de TOML vervangt. Alleen bij een inhoudelijke wijziging wordt er geschreven; bij vervanging van een bestaand bestand komt er een exacte `.bak` naast. Een bestaande geldige actieve provider blijft behouden. Werk met één app/CLI-schrijver tegelijk. Bij invoer van een nieuwe versie van een policy: laat Intune eerst de synchronisatie afronden.

Lockdown geldt **in de pilot-app**; het bestand krijgt geen andere toegangsrechten. Je kunt het met de losse CLI blijven lezen en bewerken. Bij de volgende pilotstart worden afwijkende beheerde waarden opnieuw toegepast. De vorige versie in een andere programmamap gebruikt dezelfde per-user TOML en kent deze vergrendeling niet.

Als je alle policies verwijdert, wordt de laatst toegepaste TOML je lokale uitgangspunt. De app zet een oud bestand niet automatisch terug. Wil je exact de situatie van vóór de pilot, sluit beide apps, verwijder eerst de policy en zet daarna je oorspronkelijke kopie terug. De lokale voorkeuren voor loggen zijn niet overschreven.
