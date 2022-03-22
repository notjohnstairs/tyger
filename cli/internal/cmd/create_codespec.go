package cmd

import (
	"errors"
	"fmt"
	"log"
	"net/http"
	"strconv"
	"strings"

	"dev.azure.com/msresearch/compimag/_git/tyger/cli/internal/model"
	"github.com/spf13/cobra"
	"k8s.io/apimachinery/pkg/api/resource"
)

func newCreateCodespecCommand(rootFlags *rootPersistentFlags) *cobra.Command {
	var flags struct {
		image         string
		inputBuffers  []string
		outputBuffers []string
		env           map[string]string
		command       bool
		cpu           string
		memory        string
		gpu           string
	}

	var cmd = &cobra.Command{
		Use:                   "codespec NAME --image IMAGE [[--input BUFFER_NAME] ...] [[--output BUFFER_NAME] ...] [[--env \"KEY=VALUE\"] ...] [resources] [--command] -- [COMMAND] [args...]",
		Short:                 "Create or update a codespec",
		Long:                  `Create of update a codespec. Outputs the version of the codespec that was created.`,
		DisableFlagsInUseLine: true,
		RunE: func(cmd *cobra.Command, args []string) error {
			if len(args) == 0 {
				return errors.New("a name for the codespec is required")
			}
			if len(args) > 1 && cmd.ArgsLenAtDash() == -1 {
				return errors.New("container arguments must be preceded by --")
			}

			codespecName := args[0]
			containerArgs := args[1:]

			codespec := model.Codespec{
				Image: flags.image,
				Buffers: &model.BufferParameters{
					Inputs:  flags.inputBuffers,
					Outputs: flags.outputBuffers,
				},
				Env:       flags.env,
				Resources: &model.CodespecResources{},
			}

			if flags.command {
				codespec.Command = containerArgs
			} else {
				codespec.Args = containerArgs
			}

			if (flags.cpu) != "" {
				_, err := resource.ParseQuantity(flags.cpu)
				if err != nil {
					return fmt.Errorf("cpu value is invalid: %v", err)
				}

				codespec.Resources.Cpu = &flags.cpu
			}
			if (flags.memory) != "" {
				_, err := resource.ParseQuantity(flags.memory)
				if err != nil {
					return fmt.Errorf("memory value is invalid: %v", err)
				}

				codespec.Resources.Memory = &flags.memory
			}
			if (flags.gpu) != "" {
				_, err := resource.ParseQuantity(flags.gpu)
				if err != nil {
					return fmt.Errorf("gpu value is invalid: %v", err)
				}

				codespec.Resources.Gpu = &flags.gpu
			}

			resp, err := InvokeRequest(http.MethodPut, fmt.Sprintf("v1/codespecs/%s", codespecName), codespec, &codespec, rootFlags.verbose)
			if err != nil {
				return err
			}

			version, err := getCodespecVersionFromResponse(resp)
			if err != nil {
				return fmt.Errorf("unable to get codespec version: %v", err)
			}
			fmt.Println(version)

			return nil
		},
	}

	cmd.Flags().StringVar(&flags.image, "image", "", "The container image (required)")
	if err := cmd.MarkFlagRequired("image"); err != nil {
		log.Panicln(err)
	}
	cmd.Flags().StringSliceVarP(&flags.inputBuffers, "input", "i", nil, "Input buffer parameter names")
	cmd.Flags().StringSliceVarP(&flags.outputBuffers, "output", "o", nil, "Output buffer parameter names")
	cmd.Flags().StringToStringVarP(&flags.env, "env", "e", nil, "Environment variables to set in the container in the form KEY=value")
	cmd.Flags().BoolVar(&flags.command, "command", false, "If true and extra arguments are present, use them as the 'command' field in the container, rather than the 'args' field which is the default.")
	cmd.Flags().StringVarP(&flags.cpu, "cpu", "c", "", "CPU cores needed")
	cmd.Flags().StringVarP(&flags.memory, "memory", "m", "", "memory bytes needed")
	cmd.Flags().StringVarP(&flags.gpu, "gpu", "g", "", "GPUs needed")

	return cmd
}

func getCodespecVersionFromResponse(resp *http.Response) (int, error) {
	location := resp.Header.Get("Location")
	return strconv.Atoi(location[strings.LastIndex(location, "/")+1:])
}
