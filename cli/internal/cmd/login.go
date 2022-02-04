package cmd

import (
	"errors"

	"dev.azure.com/msresearch/compimag/_git/tyger/cli/internal/clicontext"
	"github.com/spf13/cobra"
)

func newLoginCommand(rootFlags *rootPersistentFlags) *cobra.Command {
	flags := clicontext.LoginOptions{}

	loginCmd := &cobra.Command{
		Use:   "login SERVER_URL",
		Short: "Login to a server",
		Long: `Login to the Tyger server at the given URL.
Subsequent commands will be performed against this server.`,
		DisableFlagsInUseLine: true,
		Args:                  exactlyOneArg("server [--service pricipal APPID --certificate CERTPATH] [--use-device-code] url"),
		RunE: func(cmd *cobra.Command, args []string) error {

			if (flags.ServicePrincipal == "") != (flags.CertificatePath == "") {
				return errors.New("--service-principal and --cert must be specified together")
			}

			if flags.ServicePrincipal != "" && flags.UseDeviceCode {
				return errors.New("--use-device-code cannot be used with --service-principal")
			}

			flags.ServerUri = args[0]
			return clicontext.Login(flags)
		},
	}

	loginCmd.AddCommand(newLoginStatusCommand(rootFlags))

	loginCmd.Flags().StringVarP(&flags.ServicePrincipal, "service-principal", "s", "", "The service principal app ID or identifier URI")
	loginCmd.Flags().StringVarP(&flags.CertificatePath, "cert", "c", "", "The path to the certificate in PEM format to use for service principal authentication")
	loginCmd.Flags().BoolVarP(&flags.UseDeviceCode, "use-device-code", "d", false, "Whether to use the device code flow for user logins. Use this mode when the app can't launch a browser on your behalf.")

	return loginCmd
}