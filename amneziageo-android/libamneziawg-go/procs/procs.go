/* SPDX-License-Identifier: MIT
 *
 * Says how many processors of the Go runtime the engine takes on a device (AmneziaGeo).
 */

package procs

import (
	"strconv"
	"strings"
)

// С этого числа ядер устройства движок берёт один процессор рантайма.
const Many = 6

// For говорит, сколько процессоров рантайма взять на устройстве с таким числом ядер; ноль оставляет выбор рантайму.
func For(cores int) int {
	if cores >= Many {
		return 1
	}
	return 0
}

// Cores считает ядра по списку, каким их называет система: «0-7», «0-3,6-7».
func Cores(list string) (int, bool) {
	count := 0
	for _, part := range strings.Split(strings.TrimSpace(list), ",") {
		first, last, ranged := strings.Cut(part, "-")
		lo, err := strconv.Atoi(first)
		if err != nil || lo < 0 {
			return 0, false
		}

		hi := lo
		if ranged {
			if hi, err = strconv.Atoi(last); err != nil || hi < lo {
				return 0, false
			}
		}

		count += hi - lo + 1
	}
	return count, true
}
