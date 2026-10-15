# DäckHem – adminpanel

Adminpanelen finns på `https://localhost:7100/admin.html` när API-projektet körs med HTTPS.

## Konfigurera adminnyckel lokalt

Admin-API:et är **låst som standard** och returnerar 503 tills en hemlig nyckel på minst 24 tecken har konfigurerats. Lägg aldrig nyckeln i GitHub eller `appsettings.json`.

I Visual Studio: högerklicka på projektet **DackHem.Api** och välj **Manage User Secrets**. Lägg till följande JSON med en egen lång slumpmässig nyckel:

```json
{
  "Admin": {
    "ApiKey": "BYT-TILL-EN-EGEN-LANG-HEMLIG-NYCKEL-MINST-24-TECKEN"
  }
}
```

Spara, starta om API:et, öppna `/admin.html` och ange samma nyckel. Nyckeln skickas bara till API:et när du begär bokningar och lagras inte i webbläsarens localStorage. Lås panelen när du är klar.

API: `GET /api/admin/bookings?page=1&pageSize=30` kräver header `X-Admin-Key`. Svaret innehåller bokningsinformation, men aldrig avbokningsnyckel eller hash.

## Säkerhet och driftsättning

Denna första version är avsedd för lokal utveckling. För en publik driftsättning bör adminåtkomst få riktig inloggning, hastighetsbegränsning och skydd mot lösenordsgissning. Kör bara över HTTPS. Lägg inte SQLite-databasen eller andra kunduppgifter i Git.

## Test

1. Utan konfigurerad nyckel: admin-API returnerar 503.
2. Med fel nyckel: admin-API returnerar 401.
3. Med rätt nyckel: adminpanelen listar bokningar inklusive avbokade, i datumordning.
4. Kontrollera att vanliga bokningar och avbokningar fortfarande fungerar.
