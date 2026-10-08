# Test-USB til touchskærm-pc

USB-nøglen er en Debian 13 live-session med jukeboksen allerede lagt på. Den booter på en almindelig 64-bit pc, starter hylden på touchskærmen og afspilningen på HDMI, og den skriver ikke til computerens egen disk. Når du slukker og tager nøglen ud, er maskinen som før.

Nøglen er til test. Den faste installation på klub-pc'ens SSD er stadig `deploy/scripts/install.sh`.

## Hent imaget

Filen hedder `jukebox-live-amd64.iso`. Den ligger ved siden af bygningen, i `publish/usb/`, når `deploy/scripts/build-usb.sh` er kørt. Samme mappe har en `.sha256`-fil.

Kontrollér filen før du skriver den:

```bash
sha256sum -c jukebox-live-amd64.iso.sha256
```

## Skriv den til en USB-nøgle

Nøglen skal være mindst 8 GB. Alt på nøglen bliver slettet. Vælg USB-nøglen, ikke computerens egen disk.

**balenaEtcher** (Windows, macOS og Linux) er den sikre måde. Vælg ISO-filen, vælg USB-nøglen, og skriv.

**Rufus** på Windows: vælg ISO-filen, og vælg DD-tilstand når Rufus spørger. ISO-tilstand giver en nøgle der ikke booter den her type live-system.

**Linux:**

```bash
lsblk
sudo dd if=jukebox-live-amd64.iso of=/dev/sdX bs=4M status=progress conv=fsync
```

`sdX` er hele nøglen, for eksempel `sdb`, ikke en partition som `sdb1`.

## Start på touchskærm-pc'en

1. Sæt nøglen i, og vælg USB i opstartsmenuen (ofte F10, F12 eller Esc).
2. Hvis UEFI afviser nøglen, så slå Secure Boot fra. Imaget er ikke signeret til Secure Boot.
3. Tilslut HDMI-tv'et når du vil se filmen. Touchskærmen viser kun hylden. Tv'et viser velkomst, kø og fuldskærm. Tv'et kan sættes i efter opstart; næste nummer lægges på HDMI.
4. Første opstart tager et par minutter. Eksempel-film bliver lavet på nøglen, så der er noget at trykke på. Hylden hedder Oak & Iron Clubhouse.
5. Administration: åbn `http://127.0.0.1:47123/admin` på maskinen. PIN-koden er **2580**. Under playback vælges lydudgang. Analog line out er førstevalget.
6. En telefon på samme netværk kan åbne `http://jukebox.local:47123`.

Konsol, hvis noget driller: Ctrl+Alt+F2, bruger `jukebox`, adgangskode `jukebox`. Tilbage til hylden er Ctrl+Alt+F1. Skærmloggen er `/home/jukebox/xorg.log`.

Sort skærm efter boot-menuen: genstart, tryk `e` på Live-linjen, tilføj `nomodeset` til kernelinjen, og start med Ctrl+X eller F10.

## Byg imaget selv

Kræver en Debian 13-maskine med net og cirka 20 GB fri disk. `live-build` skal være fra 2024 eller nyere (`lb --version` viser en dato). Ubuntu 24.04's pakke er for gammel til UEFI-billedet.

```bash
sudo apt-get install -y live-build debootstrap debian-archive-keyring xorriso isolinux syslinux-utils grub-pc-bin grub-efi-amd64-bin mtools dosfstools squashfs-tools zstd
./deploy/scripts/build-usb.sh
```

Scriptet udgiver programmet som en selvstændig linux-x64-build, lægger den ind i live-systemet og kører `lb build`. Resultatet er `publish/usb/jukebox-live-amd64.iso`. Et fuldt rebuild fra bunden er `sudo ./deploy/scripts/build-usb.sh --clean`.

Live-systemet kører i RAM og på nøgles overlay. Kø, rettelser og eksempel-film forsvinder når strømmen tages. Det er meningen for en test.
