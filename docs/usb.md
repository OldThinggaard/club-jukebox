# Installations-USB til touchskærm-pc

USB-nøglen er en Debian 13-installation. Den starter en guide på dansk, sletter den harddisk du vælger, og lægger Videojukebox på den. Bagefter kan maskinen vælge et trådløst netværk og hente film ned fra Onlime Sync.

Den tidligere nøgle startede kun programmet midlertidigt og lod computerens disk være. Denne nøgle gør det modsatte: den valgte disk bliver tømt.

## Hvad guiden gør

1. Advarer om at harddisken bliver slettet.
2. Viser de trådløse netværk. Adgangskoden tastes på skærmen. Et netværkskabel kan bruges i stedet.
3. Logger på Onlime med en engangskode fra [cloud.onlime.dk/web/secure](https://cloud.onlime.dk/web/secure). Login sker før disken slettes, så en forkert kode ikke ødelægger noget.
4. Viser de fundne diske. USB-nøglen selv, mærket JUKEBOX, er skjult.
5. Sletter den valgte disk, opretter en ny EFI- og systempartition, og kopierer Debian og Videojukebox over.
6. Genstarter. Tag nøglen ud når skærmen bliver sort, ellers starter guiden forfra.

Filmene hentes først når den nye disk er startet. Læg film i Onlime-mappen `media` og plakater i `events`. Hele Sync-mappen bliver hentet, og den kan fylde en disk på 128 GB. Sync er tovejs: filer der lægges i sync-mappen på maskinen, bliver også lagt op i Onlime.

Eksempel-film bliver ikke lagt på den installerede maskine, så de ikke ryger op i kundens Onlime.

## Hent imaget

Filen hedder `jukebox-live-amd64.iso`. Den ligger i `publish/usb/` når `deploy/scripts/build-usb.sh` er kørt. Samme mappe har en `.sha256`-fil.

```bash
sha256sum -c jukebox-live-amd64.iso.sha256
```

## Skriv den til en USB-nøgle

Nøglen skal være mindst 8 GB. Alt på nøglen bliver slettet. Vælg USB-nøglen, ikke computerens egen disk. Den sletning sker først når guiden kører på touchskærm-pc'en.

**balenaEtcher** (Windows, macOS og Linux): vælg ISO-filen, vælg USB-nøglen, og skriv.

**Rufus** på Windows: vælg ISO-filen, og vælg DD-tilstand når Rufus spørger.

**Linux:**

```bash
lsblk
sudo dd if=jukebox-live-amd64.iso of=/dev/sdX bs=4M status=progress conv=fsync
```

`sdX` er hele nøglen, for eksempel `sdb`, ikke en partition som `sdb1`.

## Start på touchskærm-pc'en

1. Sæt nøglen i, og vælg USB i opstartsmenuen (ofte F10, F12 eller Esc).
2. Hvis UEFI afviser nøglen, så slå Secure Boot fra. Imaget er ikke signeret.
3. Følg guiden. Vælg klubbens harddisk, ikke en anden USB-disk.
4. Når maskinen genstarter uden nøglen, viser touchskærmen hylden. HDMI-tv'et viser velkomst og film.
5. Administration: `http://127.0.0.1:47123/admin` på maskinen. PIN-koden er **2580**. Analog line out vælges under playback.
6. En telefon på samme netværk kan åbne `http://jukebox.local:47123`.

Hvis Onlime blev sprunget over, kan login køres senere fra konsollen:

```bash
sudo /opt/jukebox/lib/onlime-login.sh /sti/til/kodefil videojukebox
```

Konsol: Ctrl+Alt+F2, bruger `jukebox`, adgangskode `jukebox`. Tilbage til skærmen er Ctrl+Alt+F1. Skærmloggen er `/home/jukebox/xorg.log`.

Sort skærm efter boot-menuen: genstart, tryk `e` på Live-linjen, tilføj `nomodeset` til kernelinjen, og start med Ctrl+X eller F10.

## Byg imaget selv

Kræver en Debian 13-maskine med net og cirka 20 GB fri disk. `live-build` skal være fra 2024 eller nyere (`lb --version` viser en dato). Ubuntu 24.04's pakke er for gammel til UEFI-billedet.

```bash
sudo apt-get install -y live-build debootstrap debian-archive-keyring xorriso isolinux syslinux-utils grub-pc-bin grub-efi-amd64-bin mtools dosfstools squashfs-tools zstd
./deploy/scripts/build-usb.sh
```

Scriptet udgiver programmet, lægger det ind i live-systemet og kører `lb build`. Resultatet er `publish/usb/jukebox-live-amd64.iso`.
