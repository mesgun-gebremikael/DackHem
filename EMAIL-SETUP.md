# E-postbekräftelser för DäckHem

Denna funktion är **avstängd tills SMTP är konfigurerat**. Bokningar sparas ändå i databasen och svaret från API:et visar `emailSent: false` om ingen bekräftelse skickades.

## Konfiguration i Visual Studio

Högerklicka på `DackHem.Api` → **Manage User Secrets**. **Behåll befintliga Admin-inställningar** och lägg till `Email` i samma JSON-objekt:

```json
{
  "Admin": {
    "ApiKey": "BEHÅLL-DIN-EXISTERANDE-HEMLIGA-ADMINNYCKEL"
  },
  "Email": {
    "SmtpHost": "DIN-LEVERANTÖRS-SMTP-SERVER",
    "SmtpPort": 587,
    "Username": "DIN-SMTP-ANVÄNDARE",
    "Password": "DITT-SMTP-APP-LÖSENORD-ELLER-TOKEN",
    "From": "DIN-VERIFIERADE-AVSÄNDARADRESS",
    "PublicBaseUrl": "https://localhost:7100"
  }
}
```

Använd riktiga uppgifter från din e-postleverantör. Vanliga kontolösenord kanske inte fungerar: använd app-lösenord eller leverantörens SMTP-token om det stöds. Dela aldrig lösenord i chatten eller på GitHub. `PublicBaseUrl` måste bytas till din riktiga HTTPS-domän innan kunder kan använda länkarna utanför din dator.

## Flöde

- Efter att bokningen har sparats försöker servern skicka ett textmejl med bokningsinformation och avbokningslänk.
- Vid SMTP-fel ligger bokningen kvar och API:et svarar med `emailSent: false`; frontend visar avbokningsnyckeln som reservlösning.
- Avbokningslänken använder URL-fragment (`#token=...`) och sidan tar bort fragmentet ur adressfältet efter inläsning.
- Avbokning är fortfarande spärrad när 7 timmar eller mindre återstår.

## Inför publicering

Lägg till lämplig kö/retry för e-post och produktionssäker SMTP-leverantör, verifiera domän/SPF/DKIM och testa e-postleverans. Undvik att skapa upprepade bokningar när mejl misslyckas. Publicera inte en lokal SQLite-databas med kunduppgifter i GitHub.
