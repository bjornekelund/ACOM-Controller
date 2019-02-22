# ACOM Controller

Minimalistic WPF application for controlling and monitoring an *ACOM 600S* 
shortwave radio power amplifier from the PC desktop via its RS-232 remote control 
interface.

Designed for a minimal screen footprint and can be modified to consume even less. 
Also easily modifiable to control e.g. *ACOM 1200S* or the upcoming *ACOM 700S*.

Connects to COM4 by default but adding the name of a different COM
port on the command line will make it connect to this instead,
e.g. "*ACOMController COM5*". If you are using scripted start-up of
apps that does not allow command line arguments, like with 
*DXLab Launcher*, this is not a problem.

**ACOM Controller** will remember the used COM port between sessions so you only need 
to execute the app once with a command line argument to set the COM port. 
This can be done from e.g. the Windows Command prompt or Power Shell.

For more information, visit www.sm7iun.se
