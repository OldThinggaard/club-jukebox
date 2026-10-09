# Videojukebox på Debian 13

Debian 13 installeres med Debians eget program. Videojukebox lægges på bagefter med `videojukebox-pakke.zip`. Pakken partitionerer ikke disken og sletter den ikke.

Operativsystemet er **debian-13.7.0-amd64-netinst.iso** fra [Debians spejl](https://get.debian.org/images/release/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso). SHA256: `a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355`.

Trinnene, som også ligger i pakken som `LÆS-MIG.txt`:

1. Skriv Debian-filen til en USB-nøgle med Rufus i ISO-tilstand.
2. Start grafisk installation. Sprog dansk, værtsnavn `jukebox`, tastatur dansk.
3. Vælg **Guidet - benyt hele disken**. Debian sletter den gamle disk her.
4. Under programvalg: ingen skrivebord. Behold almindelige systemværktøjer.
5. Pak `videojukebox-pakke.zip` ud på en USB-nøgle, montér den, og kør `bash .../install.sh` som root. Kablet skal være sat i, fordi scriptet henter Chromium, mpv og X.
6. Genstart. Hylden kommer på touchskærmen. Administration er PIN **2580**.

Netværk og Onlime sættes op under Administration, efter programmet kører. Film lægges i `media`, plakater i `events`.

Pakken bygges med `./deploy/scripts/build-package.sh`. Den lander i `publish/pakke/videojukebox-pakke.zip`.
