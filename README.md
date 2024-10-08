# ACOM Controller

Minimalistic Windows application for controlling and monitoring *ACOM 500S*, *ACOM 600S*, 
*ACOM 700S*, *ACOM 1200S*, and *ACOM 2020S* shortwave radio power amplifiers from the 
desktop via its RS-232 remote control interface. 

With the help of an ethernet serial port client and server or a serial port tunneling 
application like `com2tcp` it can also be used for geographically remote operation. 

Right-click the *Standby* button to access the configuration panel.
Default configuration is for ACOM 700S with communication on COM1. 

By default it stays on top of other apps to e.g. allow use with a full 
screen logger on a single screen.

The "Do nothing if already running" option silently stops the program at 
start up if another copy is already running.

The app can, optionally, also display power efficiency, gain, and SWR.

For more information, visit [www.sm7iun.se](https://sm7iun.se/station/acom)
