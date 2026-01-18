# Infomation
Please use the following form when submitting a pull request for amiibo database. Update **both** the `amiibo.json` and  `game_info.json` file to prevent error. If unsure of `game_info.json` please use the example format below to include an empty array.

# Pull request form
### Checklist
 - [ ] The ids provided are not spoofed.
 - [ ] The ids provided are all in lowercase.
 - [ ] The `game_info.json` had been updated with the matching ids.
 - [ ] Images of amiibo have been provided in high quality.

### Link to amiibo.life for amiibo
- link_1
- link_2
- link_3

## Example of empty game_info
```json
"0x02ed0001015a0502":
{
	"games3DS": [],
	"gamesWiiU": [],
	"gamesSwitch": []
}
```
