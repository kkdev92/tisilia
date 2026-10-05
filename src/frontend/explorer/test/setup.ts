// The tests read the Explorer's own words in English whatever language the machine prefers (Node's navigator.language follows the
// system's locale); the checks of the Japanese words choose it themselves.
import { prefs } from "../src/prefs.js";

prefs.locale = "en";
