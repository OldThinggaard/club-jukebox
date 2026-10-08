using Jukebox.Core.Install;

namespace Jukebox.Tests;

public class InstallDiskTests
{
    [Fact]
    public void Hides_the_usb_stick_and_keeps_the_internal_disk()
    {
        const string json = """
        {"blockdevices":[
          {"name":"sda","size":"128G","type":"disk","model":"Club SSD","tran":"sata","rm":false,"label":null,"mountpoint":null,"children":[
            {"name":"sda1","label":null,"mountpoint":null,"type":"part"}
          ]},
          {"name":"sdb","size":"16G","type":"disk","model":"USB Flash","tran":"usb","rm":true,"label":null,"mountpoint":null,"children":[
            {"name":"sdb1","label":"JUKEBOX","mountpoint":"/run/live/medium","type":"part"}
          ]},
          {"name":"loop0","size":"4G","type":"disk","model":"","tran":null,"rm":false,"label":null,"mountpoint":null}
        ]}
        """;

        var disks = InstallDisks.Parse(json);
        Assert.Equal(["loop0", "sda"], disks.Select(disk => disk.Name).ToArray());
        Assert.Equal("Club SSD", disks.Single(disk => disk.Name == "sda").Model);
        Assert.False(InstallDisks.IsInstallTarget("/dev/sda1"));
        Assert.False(InstallDisks.IsInstallTarget("/dev/../sda"));
    }
}
