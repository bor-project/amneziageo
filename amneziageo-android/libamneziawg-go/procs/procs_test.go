/* SPDX-License-Identifier: MIT
 *
 * Tests of the count of runtime processors the engine takes on a device (AmneziaGeo).
 */

package procs

import "testing"

func TestADeviceOfSixCoresAndMoreGivesTheEngineOneProcessor(t *testing.T) {
	for _, cores := range []int{6, 8, 12} {
		if got := For(cores); got != 1 {
			t.Fatalf("a device of %d cores gives the engine %d processor(s)", cores, got)
		}
	}
}

func TestADeviceOfFewerCoresLeavesTheChoiceToTheRuntime(t *testing.T) {
	for _, cores := range []int{1, 2, 4, 5} {
		if got := For(cores); got != 0 {
			t.Fatalf("a device of %d cores gives the engine %d processor(s)", cores, got)
		}
	}
}

func TestTheCoresAreCountedFromTheListOfTheSystem(t *testing.T) {
	for list, want := range map[string]int{"0-7\n": 8, "0-3,6-7": 6, "0": 1, "0,2-3\n": 3, " 0-5 ": 6} {
		if got, ok := Cores(list); !ok || got != want {
			t.Fatalf("the list %q counts %d core(s), read %v", list, got, ok)
		}
	}
}

func TestAListThatNamesNoCoresIsNotCounted(t *testing.T) {
	for _, list := range []string{"", "\n", "a-b", "3-1", "-1", "0-", "0,,2", "0-3-5"} {
		if got, ok := Cores(list); ok {
			t.Fatalf("the list %q was counted as %d core(s)", list, got)
		}
	}
}
